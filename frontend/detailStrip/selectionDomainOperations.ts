import { reconcileArchitectureIcLoraStage } from "../architectures/behaviorRegistry";
import { buildArchitectureRetargetPlan } from "../architectures/catalog";
import { reconcileClipArchitectureIdentity } from "../architectures/clipIdentity";
import type { CapabilityViewResolver } from "../architectures/policy";
import { reconcileSourcedClipIdentity } from "../architectures/policy";
import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    clamp,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
} from "../constants";
import { IC_LORA_STAGE_ALL } from "../icLoraAuthoring";
import { createEntityId } from "../identity";
import { buildDefaultStage, removeRefAt } from "../normalization";
import { dispatchDocumentCommand, getTimelineStore } from "../persistence";
import { getDefaultStageModel, getRootDefaults } from "../rootDefaults";
import { setSelection } from "../selection";
import type { AudioSegment, Clip, TimelineSelection } from "../types";
import { roundToTenth } from "../utils";

export type StructuralCommit = (
    apply: (clips: Clip[]) => TimelineSelection | "render" | null,
    options?: { rebuildAfterSelect?: boolean },
) => void;

export interface DetailSelectionDomainOperations {
    deleteRefEntry(clipIdx: number, refIdx: number): void;
    deleteWindowEntry(clipIdx: number, windowIdx: number): void;
    createRetake(clipIdx: number): void;
    removeRetake(clipIdx: number): void;
    addAudioSegment(clipIdx: number): void;
    removeAudioSegment(clipIdx: number, segIdx: number): void;
    addStage(clipIdx: number): void;
    deleteStage(clipIdx: number, stageIdx: number): void;
    selectStage(clipIdx: number, stageIdx: number): void;
}

export const createDetailSelectionDomainOperations = (
    structuralCommit: StructuralCommit,
    getCapabilities: () => CapabilityViewResolver,
    renderAfterExternalCommand: () => void = () => {},
): DetailSelectionDomainOperations => {
    const commitRemoval = (
        remove: (clips: Clip[]) => number | null,
        index: number,
        neighbour: (index: number) => TimelineSelection,
        fallback: TimelineSelection,
    ): void =>
        structuralCommit((clips) => {
            const remaining = remove(clips);
            if (remaining === null) {
                return null;
            }
            return remaining > 0
                ? neighbour(Math.min(index, remaining - 1))
                : fallback;
        });

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
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (
                !clip ||
                clip.retake ||
                !getCapabilities().forClip(clip).decision("retake").supported
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
        });
    };

    const removeRetake = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip?.retake) {
                return null;
            }
            clip.retake = null;
            return { kind: "clip", clipIdx, stageIdx: 0 };
        });
    };

    const addAudioSegment = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (
                !clip ||
                !getCapabilities().forClip(clip).decision("audioSegments")
                    .supported
            ) {
                return null;
            }
            const clipDuration = Math.max(0, clip.duration || 0);
            if (clipDuration < AUDIO_SEGMENT_MIN_LENGTH) {
                return null;
            }
            const segment: AudioSegment = {
                source: null,
                startSeconds: 0,
                trimStartSeconds: 0,
                lengthSeconds: roundToTenth(
                    Math.min(AUDIO_SEGMENT_DEFAULT_LENGTH, clipDuration),
                ),
            };
            clip.audioSegments = [...(clip.audioSegments ?? []), segment];
            return {
                kind: "audio-segment",
                clipIdx,
                segIdx: clip.audioSegments.length - 1,
            };
        });
    };

    const removeAudioSegment = (clipIdx: number, segmentIdx: number): void => {
        commitRemoval(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip?.audioSegments?.[segmentIdx]) {
                    return null;
                }
                clip.audioSegments = clip.audioSegments.filter(
                    (_, index) => index !== segmentIdx,
                );
                return clip.audioSegments.length;
            },
            segmentIdx,
            (index) => ({
                kind: "audio-segment",
                clipIdx,
                segIdx: index,
            }),
            { kind: "audio", clipIdx },
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
                    reconcileArchitectureIcLoraStage(
                        clip.architecture,
                        entry,
                        !!clip.sourceVideo,
                    );
                }
                reconcileSourcedClipIdentity(clip, getCapabilities().catalog);
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
        deleteRefEntry,
        deleteWindowEntry,
        createRetake,
        removeRetake,
        addAudioSegment,
        removeAudioSegment,
        addStage,
        deleteStage,
        selectStage,
    };
};
