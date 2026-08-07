import {
    canonicalizeArchitectureIcLoraFields,
    reconcileArchitectureIncomingIcLoraDrives,
} from "../architectures/behaviorRegistry";
import { buildArchitectureRetargetPlan } from "../architectures/catalog";
import { reconcileClipArchitectureIdentity } from "../architectures/clipIdentity";
import { NONE_ARCHITECTURE_ID } from "../architectures/none/identity";
import { referenceEndpointPolicy } from "../architectures/referenceEndpoints";
import type { AuthoringTransactionSnapshot } from "../authoringSnapshot";
import { buildDefaultClipReference } from "../clipReferenceAuthoring";
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
import { defaultLoraWeight } from "../loraAuthoring";
import {
    buildDefaultRef,
    buildDefaultStage,
    getReferenceFrameMax,
} from "../normalizationStage";
import { nextAllowedReferencePosition } from "../referenceAuthoring";
import { getDefaultStageModel } from "../rootDefaults";
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
 * Each stage mirrors the clip's ref list in `frameRefStrengths`, so a ref add or
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
                      patch: {
                          frameRefStrengths: next(stage.frameRefStrengths),
                      },
                  },
              ]
            : [],
    );

export interface DetailSelectionDomainOperations {
    addRefEntry(clipIdx: number): void;
    deleteRefEntry(clipIdx: number, refIdx: number): void;
    addClipReference(clipIdx: number): void;
    deleteClipReference(clipIdx: number, referenceIdx: number): void;
    addPromptWindow(clipIdx: number): void;
    deleteWindowEntry(clipIdx: number, windowIdx: number): void;
    createRetake(clipIdx: number): void;
    removeRetake(clipIdx: number): void;
    deleteClip(clipIdx: number): void;
    addStage(clipIdx: number): void;
    deleteStage(clipIdx: number, stageIdx: number): void;
    selectStage(clipIdx: number, stageIdx: number): void;
    toggleClipSkip(clipIdx: number): void;
    toggleStageSkip(clipIdx: number, stageIdx: number): void;
}

export const createDetailSelectionDomainOperations = (
    structuralCommit: StructuralCommit,
    captureAuthoringTransaction: () => AuthoringTransactionSnapshot,
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
                const ref = clip?.frameRefs[refIdx];
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
                    remaining: clip.frameRefs.length - 1,
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
            const { capabilities, defaults } = captureAuthoringTransaction();
            if (
                !clip?.id ||
                !capabilities.forClip(clip).decision("frameReferences")
                    .supported
            ) {
                return null;
            }
            const position = nextAllowedReferencePosition(
                clip.frameRefs,
                getReferenceFrameMax(() => defaults, clip),
                referenceEndpointPolicy(clip, defaults.modelCatalog).positions,
            );
            if (position === null) {
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
                                frame: position.frame,
                                fromEnd: position.fromEnd,
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
                    refIdx: clip.frameRefs.length,
                },
            };
        });
    };

    const addClipReference = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            const { capabilities } = captureAuthoringTransaction();
            if (
                !clip?.id ||
                !capabilities.forClip(clip).decision("clipReferences").supported
            ) {
                return null;
            }
            return {
                command: {
                    type: "clip-reference.add",
                    clipId: clip.id,
                    reference: {
                        ...buildDefaultClipReference(),
                        id: createEntityId("clip_reference"),
                    },
                },
                selection: {
                    kind: "clip-ref",
                    clipIdx,
                    referenceIdx: clip.references.length,
                },
            };
        });
    };

    const deleteClipReference = (
        clipIdx: number,
        referenceIdx: number,
    ): void => {
        commitRemoval(
            (clips) => {
                const clip = clips[clipIdx];
                const reference = clip?.references[referenceIdx];
                if (!clip?.id || !reference?.id) {
                    return null;
                }
                return {
                    command: {
                        type: "clip-reference.remove",
                        clipId: clip.id,
                        referenceId: reference.id,
                    },
                    remaining: clip.references.length - 1,
                };
            },
            referenceIdx,
            (index) => ({ kind: "clip-ref", clipIdx, referenceIdx: index }),
            { kind: "clip", clipIdx, stageIdx: 0 },
        );
    };

    const addPromptWindow = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                const { capabilities } = captureAuthoringTransaction();
                if (
                    !clip?.id ||
                    !capabilities.forClip(clip).decision("promptRelay")
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
                const { capabilities } = captureAuthoringTransaction();
                if (
                    !clip?.id ||
                    clip.retake ||
                    !capabilities.forClip(clip).decision("retake").supported
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
                const keepRetakeSelected = captureAuthoringTransaction()
                    .capabilities.forClip(clip)
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

    const deleteClip = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                if (clipIdx <= 0 || clipIdx >= clips.length) {
                    return null;
                }
                const transaction = captureAuthoringTransaction();
                clips.splice(clipIdx, 1);
                reconcileArchitectureIncomingIcLoraDrives(
                    clips,
                    transaction.generatedEntryMode,
                    transaction.capabilities.catalog,
                );
                return selectionAfterRemoval(
                    clipIdx,
                    clips.length,
                    (index) => ({
                        kind: "clip",
                        clipIdx: index,
                        stageIdx: 0,
                    }),
                    { kind: "none" },
                );
            },
            { rebuildAfterSelect: true },
        );
    };

    const addStage = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip) {
                    return null;
                }
                const { capabilities, defaults } =
                    captureAuthoringTransaction();
                const last = clip.stages[clip.stages.length - 1] ?? null;
                const clipArchitectureId =
                    capabilities.forClip(clip).architectureId;
                const lockedArchitecture =
                    clipArchitectureId === NONE_ARCHITECTURE_ID ||
                    clipArchitectureId === "unsupported"
                        ? undefined
                        : clipArchitectureId;
                const stage = buildDefaultStage(
                    () => defaults,
                    (values) =>
                        getDefaultStageModel(
                            values,
                            lockedArchitecture,
                            defaults.modelCatalog,
                        ),
                    last,
                    clip.frameRefs.length,
                    clip.loras.map((entry) =>
                        defaultLoraWeight(defaults, entry.name),
                    ),
                    clip.icLoras.map((entry) =>
                        defaultLoraWeight(defaults, entry.lora),
                    ),
                );
                stage.skipped = last?.skipped === true;
                if (
                    clipArchitectureId === NONE_ARCHITECTURE_ID &&
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
                    stageIdx <= 0 ||
                    stageIdx >= clip.stages.length
                ) {
                    return null;
                }
                const transaction = captureAuthoringTransaction();
                clip.stages.splice(stageIdx, 1);
                for (const entry of clip.icLoras) {
                    if (entry.stage === stageIdx) {
                        entry.stage = IC_LORA_STAGE_ALL;
                    } else if (entry.stage > stageIdx) {
                        entry.stage -= 1;
                    }
                    canonicalizeArchitectureIcLoraFields(
                        transaction.capabilities.forClip(clip).architectureId,
                        entry,
                    );
                }
                reconcileClipArchitectureIdentity(
                    clip,
                    transaction.capabilities.catalog,
                );
                reconcileArchitectureIncomingIcLoraDrives(
                    clips,
                    transaction.generatedEntryMode,
                    transaction.capabilities.catalog,
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

    const toggleClipSkip = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clipId = clips[clipIdx]?.id;
            return clipId
                ? {
                      command: { type: "clip.toggle-skip", clipId },
                      selection: "render",
                  }
                : null;
        });
    };

    const toggleStageSkip = (clipIdx: number, stageIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            const stageId = clip?.stages[stageIdx]?.id;
            return clip?.id && stageId
                ? {
                      command: {
                          type: "stage.toggle-skip",
                          clipId: clip.id,
                          stageId,
                      },
                      selection: "render",
                  }
                : null;
        });
    };

    return {
        addRefEntry,
        deleteRefEntry,
        addClipReference,
        deleteClipReference,
        addPromptWindow,
        deleteWindowEntry,
        createRetake,
        removeRetake,
        deleteClip,
        addStage,
        deleteStage,
        selectStage,
        toggleClipSkip,
        toggleStageSkip,
    };
};
