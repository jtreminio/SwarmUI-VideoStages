import {
    canonicalizeArchitectureIcLoraFields,
    reconcileArchitectureIncomingIcLoraDrives,
} from "../architectures/behaviorRegistry";
import { buildArchitectureRetargetPlan } from "../architectures/catalog";
import { reconcileClipArchitectureIdentity } from "../architectures/clipIdentity";
import type { CapabilityViewResolver } from "../architectures/policy";
import { reconcileSourcedClipIdentity } from "../architectures/policy";
import {
    clamp,
    PROMPT_WINDOW_DEFAULT_DURATION,
    PROMPT_WINDOW_MIN_DURATION,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
} from "../constants";
import { IC_LORA_STAGE_ALL } from "../icLoraAuthoring";
import { createEntityId } from "../identity";
import {
    appendRefToClip,
    buildDefaultRef,
    buildDefaultStage,
    removeRefAt,
} from "../normalization";
import { dispatchDocumentCommand, getTimelineStore } from "../persistence";
import { getDefaultStageModel, getRootDefaults } from "../rootDefaults";
import { setSelection } from "../selection";
import type { Clip, TimelineSelection } from "../types";
import { roundToTenth } from "../utils";

export type StructuralCommit = (
    apply: (clips: Clip[]) => TimelineSelection | "render" | null,
    options?: { rebuildAfterSelect?: boolean },
) => void;

export interface DetailSelectionDomainOperations {
    addRefEntry(clipIdx: number): void;
    deleteRefEntry(clipIdx: number, refIdx: number): void;
    addPromptWindow(clipIdx: number): void;
    deleteWindowEntry(clipIdx: number, windowIdx: number): void;
    createRetake(clipIdx: number): void;
    removeRetake(clipIdx: number): void;
    addStage(clipIdx: number): void;
    deleteStage(clipIdx: number, stageIdx: number): void;
    selectStage(clipIdx: number, stageIdx: number): void;
}

export const createDetailSelectionDomainOperations = (
    structuralCommit: StructuralCommit,
    getCapabilities: () => CapabilityViewResolver,
    renderAfterExternalCommand: () => void = () => {},
    getGeneratedEntryMode: () => "text-to-video" | "image-to-video" = () =>
        "text-to-video",
): DetailSelectionDomainOperations => {
    const commitRemoval = (
        remove: (clips: Clip[]) => number | null,
        index: number,
        neighbour: (index: number) => TimelineSelection,
        fallback: TimelineSelection,
    ): void =>
        structuralCommit(
            (clips) => {
                const remaining = remove(clips);
                if (remaining === null) {
                    return null;
                }
                return remaining > 0
                    ? neighbour(Math.min(index, remaining - 1))
                    : fallback;
            },
            // Deleting the last inactive item, or the first of several items,
            // can leave the selected numeric index unchanged. A normal
            // setSelection then emits no event, so force the repeater DOM to
            // rebuild around the surviving entities.
            { rebuildAfterSelect: true },
        );

    const deleteRefEntry = (clipIdx: number, refIdx: number): void => {
        commitRemoval(
            (clips) => {
                const clip = clips[clipIdx];
                return clip && removeRefAt(clip, refIdx)
                    ? clip.refs.length
                    : null;
            },
            refIdx,
            (index) => ({ kind: "ref", clipIdx, refIdx: index }),
            { kind: "clip", clipIdx, stageIdx: 0 },
        );
    };

    const addRefEntry = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (
                !clip ||
                !getCapabilities().forClip(clip).decision("frameReferences")
                    .supported
            ) {
                return null;
            }
            appendRefToClip(clip, buildDefaultRef());
            return {
                kind: "ref",
                clipIdx,
                refIdx: clip.refs.length - 1,
            };
        });
    };

    const addPromptWindow = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (
                    !clip ||
                    !getCapabilities().forClip(clip).decision("promptRelay")
                        .supported
                ) {
                    return null;
                }
                const clipDuration = Math.max(0, clip.duration || 0);
                const windows = [...(clip.promptWindows ?? [])].sort(
                    (a, b) => a.start - b.start,
                );
                let start = 0;
                let end = clipDuration;
                for (const window of windows) {
                    const windowStart = clamp(window.start, 0, clipDuration);
                    if (windowStart - start >= PROMPT_WINDOW_MIN_DURATION) {
                        end = windowStart;
                        break;
                    }
                    start = Math.max(
                        start,
                        clamp(window.start + window.duration, 0, clipDuration),
                    );
                }
                if (end === clipDuration) {
                    const next = windows.find(
                        (window) =>
                            window.start >= start + PROMPT_WINDOW_MIN_DURATION,
                    );
                    if (next) {
                        end = clamp(next.start, start, clipDuration);
                    }
                }
                if (end - start < PROMPT_WINDOW_MIN_DURATION) {
                    return null;
                }
                const window = {
                    prompt: "",
                    start: roundToTenth(start),
                    duration: roundToTenth(
                        Math.min(PROMPT_WINDOW_DEFAULT_DURATION, end - start),
                    ),
                };
                clip.promptWindows.push(window);
                clip.promptWindows.sort((a, b) => a.start - b.start);
                return {
                    kind: "prompt-minor",
                    clipIdx,
                    windowIdx: clip.promptWindows.indexOf(window),
                };
            },
            // A newly sorted leading window can reuse the currently selected
            // numeric index; rebuild even when setSelection would be a no-op.
            { rebuildAfterSelect: true },
        );
    };

    const deleteWindowEntry = (clipIdx: number, windowIdx: number): void => {
        commitRemoval(
            (clips) => {
                const windows = clips[clipIdx]?.promptWindows;
                if (!windows || windowIdx < 0 || windowIdx >= windows.length) {
                    return null;
                }
                windows.splice(windowIdx, 1);
                return windows.length;
            },
            windowIdx,
            (index) => ({
                kind: "prompt-minor",
                clipIdx,
                windowIdx: index,
            }),
            { kind: "prompt-major", clipIdx },
        );
    };

    const createRetake = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (
                    !clip ||
                    clip.retake ||
                    !getCapabilities().forClip(clip).decision("retake")
                        .supported
                ) {
                    return null;
                }
                const clipDuration = Math.max(0, clip.duration || 0);
                clip.retake = {
                    startSeconds: 0,
                    lengthSeconds: Math.max(
                        RETAKE_MIN_DURATION,
                        Math.min(
                            RETAKE_DEFAULT_DURATION,
                            clipDuration || RETAKE_DEFAULT_DURATION,
                        ),
                    ),
                    strength: RETAKE_STRENGTH_DEFAULT,
                };
                return { kind: "retake", clipIdx };
            },
            { rebuildAfterSelect: true },
        );
    };

    const removeRetake = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip?.retake) {
                    return null;
                }
                const keepRetakeSelected = getCapabilities()
                    .forClip(clip)
                    .decision("retake").supported;
                clip.retake = null;
                return keepRetakeSelected
                    ? { kind: "retake", clipIdx }
                    : { kind: "clip", clipIdx, stageIdx: 0 };
            },
            { rebuildAfterSelect: true },
        );
    };

    const selectStage = (clipIdx: number, stageIdx: number): void => {
        setSelection({ kind: "clip", clipIdx, stageIdx });
    };

    const addStage = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip) {
                    return null;
                }
                if (
                    clip.stages.length > 0 &&
                    !getCapabilities().forClip(clip).decision("multiStage")
                        .supported
                ) {
                    return null;
                }
                const last = clip.stages[clip.stages.length - 1] ?? null;
                const defaults = getRootDefaults();
                const lockedArchitecture =
                    clip.architecture === "none"
                        ? undefined
                        : clip.architecture;
                const stage = buildDefaultStage(
                    getRootDefaults,
                    (values) =>
                        getDefaultStageModel(values, lockedArchitecture),
                    last,
                    clip.refs.length,
                );
                if (clip.architecture === "none" && clip.stages.length === 0) {
                    const target = buildArchitectureRetargetPlan(
                        defaults.modelCatalog,
                        stage.model,
                    );
                    const snapshot = getTimelineStore().getSnapshot();
                    const clipId = snapshot.state.clips[clipIdx]?.id;
                    if (!target || !clipId) {
                        return null;
                    }
                    const canonicalStage = {
                        ...stage,
                        id: createEntityId("stage"),
                        modelProfileId: target.modelProfileId,
                    };
                    const result = dispatchDocumentCommand(
                        {
                            type: "batch",
                            commands: [
                                {
                                    type: "clip.convert-architecture",
                                    clipId,
                                    target,
                                },
                                {
                                    type: "stage.add",
                                    clipId,
                                    stage: canonicalStage,
                                },
                            ],
                        },
                        {
                            expectedRevision: snapshot.revision,
                            origin: "detail-strip",
                        },
                    );
                    if (result.applied) {
                        setSelection({
                            kind: "clip",
                            clipIdx,
                            stageIdx: 0,
                        });
                        renderAfterExternalCommand();
                    }
                    // The named batch already performed the one carrier write.
                    return null;
                }
                clip.stages.push(stage);
                if (
                    !reconcileClipArchitectureIdentity(
                        clip,
                        defaults.modelCatalog,
                    )
                ) {
                    return null;
                }
                return {
                    kind: "clip",
                    clipIdx,
                    stageIdx: clip.stages.length - 1,
                };
            },
            { rebuildAfterSelect: true },
        );
    };

    const deleteStage = (clipIdx: number, stageIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (
                    !clip ||
                    clip.stages.length === 0 ||
                    (clip.stages.length === 1 && clip.sourceVideo === null) ||
                    stageIdx < 0 ||
                    stageIdx >= clip.stages.length
                ) {
                    return null;
                }
                clip.stages.splice(stageIdx, 1);
                for (const entry of clip.icLoras) {
                    if (entry.stage === stageIdx) {
                        entry.stage = IC_LORA_STAGE_ALL;
                    } else if (entry.stage > stageIdx) {
                        entry.stage -= 1;
                    }
                    canonicalizeArchitectureIcLoraFields(
                        clip.architecture,
                        entry,
                    );
                }
                reconcileSourcedClipIdentity(clip, getCapabilities().catalog);
                reconcileArchitectureIncomingIcLoraDrives(
                    clips,
                    getGeneratedEntryMode(),
                );
                return {
                    kind: "clip",
                    clipIdx,
                    stageIdx:
                        clip.stages.length === 0
                            ? 0
                            : clamp(stageIdx, 0, clip.stages.length - 1),
                };
            },
            { rebuildAfterSelect: true },
        );
    };

    return {
        addRefEntry,
        deleteRefEntry,
        addPromptWindow,
        deleteWindowEntry,
        createRetake,
        removeRetake,
        addStage,
        deleteStage,
        selectStage,
    };
};
