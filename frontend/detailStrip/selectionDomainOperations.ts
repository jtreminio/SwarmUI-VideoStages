import {
    canonicalizeArchitectureIcLoraFields,
    reconcileArchitectureIncomingIcLoraDrives,
} from "../architectures/behaviorRegistry";
import { buildArchitectureRetargetPlan } from "../architectures/catalog";
import { reconcileClipArchitectureIdentity } from "../architectures/clipIdentity";
import { NONE_ARCHITECTURE_ID } from "../architectures/none/definition";
import type { CapabilityViewResolver } from "../architectures/policy";
import { reconcileSourcedClipIdentity } from "../architectures/policy";
import type { ArchitectureModelCatalog } from "../architectures/types";
import {
    clamp,
    PROMPT_WINDOW_DEFAULT_DURATION,
    PROMPT_WINDOW_MIN_DURATION,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_DEFAULT,
} from "../constants";
import type { DocumentCommand } from "../documentCommands";
import { IC_LORA_STAGE_ALL } from "../icLoraAuthoring";
import { createEntityId } from "../identity";
import { buildDefaultRef, buildDefaultStage } from "../normalization";
import { getDefaultStageModel, getRootDefaults } from "../rootDefaults";
import { selectionAfterRemoval, setSelection } from "../selection";
import type { Clip, TimelineSelection } from "../types";
import { roundToTenth } from "../utils";
import type { StructuralCommand } from "./draftQueue";

export type StructuralCommit = (
    apply: (
        clips: Clip[],
    ) => TimelineSelection | "render" | null | StructuralCommand,
    options?: { rebuildAfterSelect?: boolean },
) => void;

/**
 * Each stage mirrors the clip's ref list in `refStrengths`, so a ref add or
 * delete carries the matching per-stage patches in the same named batch.
 */
const refStrengthPatches = (
    clip: Clip,
    next: (strengths: number[]) => number[],
): DocumentCommand[] =>
    clip.stages.flatMap((stage) =>
        stage.id
            ? [
                  {
                      type: "stage.patch" as const,
                      clipId: clip.id as string,
                      stageId: stage.id,
                      patch: { refStrengths: next(stage.refStrengths) },
                  },
              ]
            : [],
    );

/**
 * Flips a clip's skip flag and reconciles the IC-LoRA drives that depend on
 * which clips still execute. The one clip-skip mutation: the detail strip and
 * the timeline's region button both apply it, they only differ in how they
 * persist the clips array.
 */
export const applyClipSkip = (
    clips: Clip[],
    clipIdx: number,
    generatedEntryMode: "text-to-video" | "image-to-video",
): boolean => {
    const clip = clips[clipIdx];
    if (!clip) {
        return false;
    }
    clip.skipped = !clip.skipped;
    reconcileArchitectureIncomingIcLoraDrives(clips, generatedEntryMode);
    return true;
};

/** The stage-level counterpart; a skipped stage can change the clip's identity. */
export const applyStageSkip = (
    clips: Clip[],
    clipIdx: number,
    stageIdx: number,
    catalog: ArchitectureModelCatalog,
    generatedEntryMode: "text-to-video" | "image-to-video",
): boolean => {
    const clip = clips[clipIdx];
    const stage = clip?.stages[stageIdx];
    if (!clip || !stage) {
        return false;
    }
    stage.skipped = !stage.skipped;
    reconcileSourcedClipIdentity(clip, catalog);
    reconcileArchitectureIncomingIcLoraDrives(clips, generatedEntryMode);
    return true;
};

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
    toggleClipSkip(clipIdx: number): void;
    toggleStageSkip(clipIdx: number, stageIdx: number): void;
}

export const createDetailSelectionDomainOperations = (
    structuralCommit: StructuralCommit,
    getCapabilities: () => CapabilityViewResolver,
    getGeneratedEntryMode: () => "text-to-video" | "image-to-video" = () =>
        "text-to-video",
): DetailSelectionDomainOperations => {
    const commitRemoval = (
        build: (clips: Clip[]) => {
            command: DocumentCommand;
            remaining: number;
        } | null,
        index: number,
        neighbour: (index: number) => TimelineSelection,
        fallback: TimelineSelection,
    ): void =>
        structuralCommit(
            (clips) => {
                const removal = build(clips);
                return removal === null
                    ? null
                    : {
                          command: removal.command,
                          selection: selectionAfterRemoval(
                              index,
                              removal.remaining,
                              neighbour,
                              fallback,
                          ),
                      };
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
                const ref = clip?.refs[refIdx];
                if (!clip?.id || !ref?.id) {
                    return null;
                }
                return {
                    command: {
                        type: "batch",
                        commands: [
                            {
                                type: "ref.remove",
                                clipId: clip.id,
                                refId: ref.id,
                            },
                            ...refStrengthPatches(clip, (strengths) =>
                                strengths.filter(
                                    (_, index) => index !== refIdx,
                                ),
                            ),
                        ],
                    },
                    remaining: clip.refs.length - 1,
                };
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
                !clip?.id ||
                !getCapabilities().forClip(clip).decision("frameReferences")
                    .supported
            ) {
                return null;
            }
            return {
                command: {
                    type: "batch",
                    commands: [
                        {
                            type: "ref.add",
                            clipId: clip.id,
                            ref: {
                                ...buildDefaultRef(),
                                id: createEntityId("ref"),
                            },
                        },
                        ...refStrengthPatches(clip, (strengths) => [
                            ...strengths,
                            STAGE_REF_STRENGTH_DEFAULT,
                        ]),
                    ],
                },
                selection: {
                    kind: "ref",
                    clipIdx,
                    refIdx: clip.refs.length,
                },
            };
        });
    };

    const addPromptWindow = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (
                    !clip?.id ||
                    !getCapabilities().forClip(clip).decision("promptRelay")
                        .supported
                ) {
                    return null;
                }
                const clipDuration = Math.max(0, clip.duration || 0);
                // Windows are persisted through the prompt carrier in start
                // order, so the existing array is already sorted.
                const windows = clip.promptWindows ?? [];
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
                    id: createEntityId("prompt_window"),
                    prompt: "",
                    start: roundToTenth(start),
                    duration: roundToTenth(
                        Math.min(PROMPT_WINDOW_DEFAULT_DURATION, end - start),
                    ),
                };
                const insertAt = windows.findIndex(
                    (candidate) => candidate.start > window.start,
                );
                const beforeWindowId =
                    insertAt < 0 ? null : (windows[insertAt].id ?? null);
                return {
                    command: {
                        type: "prompt-window.add",
                        clipId: clip.id,
                        window,
                        beforeWindowId,
                    },
                    selection: {
                        kind: "prompt-minor",
                        clipIdx,
                        windowIdx: insertAt < 0 ? windows.length : insertAt,
                    },
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
                const clip = clips[clipIdx];
                const window = clip?.promptWindows?.[windowIdx];
                if (!clip?.id || !window?.id) {
                    return null;
                }
                return {
                    command: {
                        type: "prompt-window.remove",
                        clipId: clip.id,
                        windowId: window.id,
                    },
                    remaining: clip.promptWindows.length - 1,
                };
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
                    !clip?.id ||
                    clip.retake ||
                    !getCapabilities().forClip(clip).decision("retake")
                        .supported
                ) {
                    return null;
                }
                const clipDuration = Math.max(0, clip.duration || 0);
                return {
                    command: {
                        type: "retake.add",
                        clipId: clip.id,
                        retake: {
                            id: createEntityId("retake"),
                            startSeconds: 0,
                            lengthSeconds: Math.max(
                                RETAKE_MIN_DURATION,
                                Math.min(
                                    RETAKE_DEFAULT_DURATION,
                                    clipDuration || RETAKE_DEFAULT_DURATION,
                                ),
                            ),
                            strength: RETAKE_STRENGTH_DEFAULT,
                        },
                    },
                    selection: { kind: "retake", clipIdx },
                };
            },
            { rebuildAfterSelect: true },
        );
    };

    const removeRetake = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip?.id || !clip.retake?.id) {
                    return null;
                }
                const keepRetakeSelected = getCapabilities()
                    .forClip(clip)
                    .decision("retake").supported;
                return {
                    command: {
                        type: "retake.remove",
                        clipId: clip.id,
                        retakeId: clip.retake.id,
                    },
                    selection: keepRetakeSelected
                        ? { kind: "retake", clipIdx }
                        : { kind: "clip", clipIdx, stageIdx: 0 },
                };
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
                    clip.architecture === NONE_ARCHITECTURE_ID
                        ? undefined
                        : clip.architecture;
                const stage = buildDefaultStage(
                    getRootDefaults,
                    (values) =>
                        getDefaultStageModel(values, lockedArchitecture),
                    last,
                    clip.refs.length,
                );
                if (
                    clip.architecture === NONE_ARCHITECTURE_ID &&
                    clip.stages.length === 0
                ) {
                    const target = buildArchitectureRetargetPlan(
                        defaults.modelCatalog,
                        stage.model,
                    );
                    if (!target || !clip.id) {
                        return null;
                    }
                    return {
                        command: {
                            type: "batch",
                            commands: [
                                {
                                    type: "clip.convert-architecture",
                                    clipId: clip.id,
                                    target,
                                },
                                {
                                    type: "stage.add",
                                    clipId: clip.id,
                                    stage: {
                                        ...stage,
                                        id: createEntityId("stage"),
                                        modelProfileId: target.modelProfileId,
                                    },
                                },
                            ],
                        },
                        selection: { kind: "clip", clipIdx, stageIdx: 0 },
                    };
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

    /**
     * Runs one of the shared skip mutations on a working copy and turns it
     * into the named skip command plus whatever IC-LoRA drive reconciliation
     * it forced on the other clips.
     */
    const commitSkip = (
        clips: Clip[],
        mutate: (clips: Clip[]) => boolean,
        skipCommand: (clips: Clip[]) => DocumentCommand | null,
    ): StructuralCommand | null => {
        const beforeDrives = clips.map((clip) => JSON.stringify(clip.icLoras));
        if (!mutate(clips)) {
            return null;
        }
        const skip = skipCommand(clips);
        if (!skip) {
            return null;
        }
        return {
            command: {
                type: "batch",
                commands: [
                    skip,
                    ...clips.flatMap((clip, index) =>
                        clip.id &&
                        JSON.stringify(clip.icLoras) !== beforeDrives[index]
                            ? [
                                  {
                                      type: "clip.patch" as const,
                                      clipId: clip.id,
                                      patch: { icLoras: clip.icLoras },
                                  },
                              ]
                            : [],
                    ),
                ],
            },
            selection: "render",
        };
    };

    const toggleClipSkip = (clipIdx: number): void => {
        structuralCommit((clips) =>
            commitSkip(
                clips,
                (working) =>
                    applyClipSkip(working, clipIdx, getGeneratedEntryMode()),
                (working) => {
                    const clip = working[clipIdx];
                    return clip.id
                        ? {
                              type: "clip.patch",
                              clipId: clip.id,
                              patch: { skipped: clip.skipped },
                          }
                        : null;
                },
            ),
        );
    };

    const toggleStageSkip = (clipIdx: number, stageIdx: number): void => {
        structuralCommit((clips) =>
            commitSkip(
                clips,
                (working) =>
                    applyStageSkip(
                        working,
                        clipIdx,
                        stageIdx,
                        getCapabilities().catalog,
                        getGeneratedEntryMode(),
                    ),
                (working) => {
                    const clip = working[clipIdx];
                    const stage = clip.stages[stageIdx];
                    // The reducer re-derives the clip's identity from the
                    // surviving active stages, exactly as applyStageSkip did.
                    return clip.id && stage.id
                        ? {
                              type: "stage.patch",
                              clipId: clip.id,
                              stageId: stage.id,
                              patch: { skipped: stage.skipped },
                          }
                        : null;
                },
            ),
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
        toggleClipSkip,
        toggleStageSkip,
    };
};
