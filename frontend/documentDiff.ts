import {
    deriveClipArchitectureIdentity,
    reconcileClipArchitectureIdentity,
} from "./architectures/clipIdentity";
import { planArchitectureConversion } from "./architectures/conversion/plan";
import { forceCrossArchitectureCutsForConversion } from "./architectures/policy/boundaryPolicy";
import type { ArchitectureModelCatalog } from "./architectures/types";
import type { CommandFailure, DocumentCommand } from "./documentCommands";
import type {
    CanonicalAudioSegment,
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan,
    CanonicalClip,
    CanonicalPromptWindow,
    CanonicalRefImage,
    CanonicalRetake,
    CanonicalStage,
    CanonicalVideoStagesConfig,
} from "./types";

export type DocumentBatchCommand = Extract<DocumentCommand, { type: "batch" }>;
export type DocumentDiffFailure = Extract<
    CommandFailure,
    "duplicate-id" | "invalid-id" | "architecture-invariant"
>;

export class DocumentDiffError extends Error {
    readonly failure: DocumentDiffFailure;

    constructor(failure: DocumentDiffFailure) {
        super(`Cannot diff authoring documents: ${failure}`);
        this.name = "DocumentDiffError";
        this.failure = failure;
    }
}

interface CommandPhases {
    conversions: DocumentCommand[];
    removes: DocumentCommand[];
    adds: DocumentCommand[];
    moves: DocumentCommand[];
    patches: DocumentCommand[];
}

export interface DocumentDiffContext {
    architectureCatalog: ArchitectureModelCatalog | null;
}

interface EntityListCallbacks<T extends { id: string }> {
    remove(id: string): DocumentCommand;
    add(entity: T, beforeId: string | null): DocumentCommand;
    move(id: string, beforeId: string | null): DocumentCommand;
    patch(before: T, after: T): void;
}

const ROOT_PATCH_KEYS = [
    "schemaVersion",
    "width",
    "height",
    "fps",
    "dimsExplicit",
] as const satisfies readonly (keyof CanonicalVideoStagesConfig)[];

const CLIP_PATCH_KEYS = [
    "skipped",
    "hue",
    "boundaryOut",
    "boundaryOutCarryAudio",
    "boundaryOutOverlap",
    "duration",
    "audioSource",
    "icLoras",
    "saveAudioTrack",
    "clipLengthFromAudio",
    "clipLengthFromControlNet",
    "reuseAudio",
    "uploadedAudio",
    "prompt",
    "sourceVideo",
] as const satisfies readonly (keyof CanonicalClip)[];

const STAGE_PATCH_KEYS = [
    "skipped",
    "control",
    "controlNetStrength",
    "icLoraStrengths",
    "refStrengths",
    "upscale",
    "upscaleMethod",
    "steps",
    "cfgScale",
    "sampler",
    "scheduler",
    "loras",
] as const satisfies readonly (keyof CanonicalStage)[];

const REF_PATCH_KEYS = [
    "source",
    "uploadFileName",
    "uploadedImage",
    "frame",
    "fromEnd",
] as const satisfies readonly (keyof CanonicalRefImage)[];

const AUDIO_SEGMENT_PATCH_KEYS = [
    "source",
    "startSeconds",
    "trimStartSeconds",
    "lengthSeconds",
    "volume",
] as const satisfies readonly (keyof CanonicalAudioSegment)[];

const PROMPT_WINDOW_PATCH_KEYS = [
    "prompt",
    "start",
    "duration",
] as const satisfies readonly (keyof CanonicalPromptWindow)[];

const RETAKE_PATCH_KEYS = [
    "startSeconds",
    "lengthSeconds",
    "strength",
] as const satisfies readonly (keyof CanonicalRetake)[];

const AUDIO_TRACK_PATCH_KEYS = [
    "source",
    "volume",
] as const satisfies readonly (keyof CanonicalAudioTrack)[];

const AUDIO_SPAN_PATCH_KEYS = [
    "firstClipId",
    "lastClipId",
    "timelineStartSeconds",
    "timelineLengthSeconds",
    "sourceStartSeconds",
    "clipStartOffsetSeconds",
    "clipLengthSeconds",
] as const satisfies readonly (keyof CanonicalAudioTrackSpan)[];

type AssertClassified<T, U extends keyof T> = [Exclude<keyof T, U>] extends [
    never,
]
    ? true
    : Exclude<keyof T, U>;

const _rootKeysExhaustive: AssertClassified<
    CanonicalVideoStagesConfig,
    (typeof ROOT_PATCH_KEYS)[number] | "clips" | "audioTracks"
> = true;
const _clipKeysExhaustive: AssertClassified<
    CanonicalClip,
    | "id"
    | "architecture"
    | "modelProfileId"
    | (typeof CLIP_PATCH_KEYS)[number]
    | "audioSegments"
    | "promptWindows"
    | "retake"
    | "refs"
    | "stages"
> = true;
const _stageKeysExhaustive: AssertClassified<
    CanonicalStage,
    "id" | "model" | "modelProfileId" | (typeof STAGE_PATCH_KEYS)[number]
> = true;
const _refKeysExhaustive: AssertClassified<
    CanonicalRefImage,
    "id" | (typeof REF_PATCH_KEYS)[number]
> = true;
const _audioSegmentKeysExhaustive: AssertClassified<
    CanonicalAudioSegment,
    "id" | (typeof AUDIO_SEGMENT_PATCH_KEYS)[number]
> = true;
const _promptWindowKeysExhaustive: AssertClassified<
    CanonicalPromptWindow,
    "id" | (typeof PROMPT_WINDOW_PATCH_KEYS)[number]
> = true;
const _retakeKeysExhaustive: AssertClassified<
    CanonicalRetake,
    "id" | (typeof RETAKE_PATCH_KEYS)[number]
> = true;
const _audioTrackKeysExhaustive: AssertClassified<
    CanonicalAudioTrack,
    "id" | "spans" | (typeof AUDIO_TRACK_PATCH_KEYS)[number]
> = true;
const _audioSpanKeysExhaustive: AssertClassified<
    CanonicalAudioTrackSpan,
    "id" | (typeof AUDIO_SPAN_PATCH_KEYS)[number]
> = true;
void [
    _rootKeysExhaustive,
    _clipKeysExhaustive,
    _stageKeysExhaustive,
    _refKeysExhaustive,
    _audioSegmentKeysExhaustive,
    _promptWindowKeysExhaustive,
    _retakeKeysExhaustive,
    _audioTrackKeysExhaustive,
    _audioSpanKeysExhaustive,
];

const clone = <T>(value: T): T => structuredClone(value);

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

const deepEqual = (left: unknown, right: unknown): boolean => {
    if (Object.is(left, right)) {
        return true;
    }
    if (Array.isArray(left) || Array.isArray(right)) {
        return (
            Array.isArray(left) &&
            Array.isArray(right) &&
            left.length === right.length &&
            left.every((value, index) => deepEqual(value, right[index]))
        );
    }
    if (!isRecord(left) || !isRecord(right)) {
        return false;
    }
    const leftKeys = Object.keys(left).sort();
    const rightKeys = Object.keys(right).sort();
    return (
        leftKeys.length === rightKeys.length &&
        leftKeys.every(
            (key, index) =>
                key === rightKeys[index] && deepEqual(left[key], right[key]),
        )
    );
};

const changedPatch = <T extends object, K extends keyof T>(
    before: T,
    after: T,
    keys: readonly K[],
): Partial<Pick<T, K>> => {
    const patch: Partial<Pick<T, K>> = {};
    for (const key of keys) {
        if (!deepEqual(before[key], after[key])) {
            (patch as Record<PropertyKey, unknown>)[key] = clone(after[key]);
        }
    }
    return patch;
};

const hasPatch = (patch: object): boolean => Object.keys(patch).length > 0;

const allEntityIds = (document: CanonicalVideoStagesConfig): unknown[] => [
    ...document.clips.flatMap((clip) => [
        clip.id,
        ...clip.stages.map((stage) => stage.id),
        ...clip.refs.map((ref) => ref.id),
        ...clip.audioSegments.map((segment) => segment.id),
        ...clip.promptWindows.map((window) => window.id),
        ...(clip.retake ? [clip.retake.id] : []),
    ]),
    ...document.audioTracks.flatMap((track) => [
        track.id,
        ...track.spans.map((span) => span.id),
    ]),
];

const validateDocumentIds = (document: CanonicalVideoStagesConfig): void => {
    const ids = allEntityIds(document);
    if (
        ids.some(
            (id) =>
                typeof id !== "string" ||
                id.trim().length === 0 ||
                id.trim() !== id,
        )
    ) {
        throw new DocumentDiffError("invalid-id");
    }
    if (new Set(ids).size !== ids.length) {
        throw new DocumentDiffError("duplicate-id");
    }
};

const insertBeforeId = (
    ids: string[],
    id: string,
    beforeId: string | null,
): void => {
    if (beforeId === null) {
        ids.push(id);
        return;
    }
    const index = ids.indexOf(beforeId);
    ids.splice(index, 0, id);
};

const moveBeforeId = (
    ids: string[],
    id: string,
    beforeId: string | null,
): void => {
    ids.splice(ids.indexOf(id), 1);
    insertBeforeId(ids, id, beforeId);
};

const diffEntityList = <T extends { id: string }>(
    before: readonly T[],
    after: readonly T[],
    phases: CommandPhases,
    callbacks: EntityListCallbacks<T>,
): void => {
    const beforeById = new Map(before.map((entity) => [entity.id, entity]));
    const afterIds = new Set(after.map((entity) => entity.id));
    const currentIds = before.map((entity) => entity.id);

    for (const entity of before) {
        if (!afterIds.has(entity.id)) {
            phases.removes.push(callbacks.remove(entity.id));
            currentIds.splice(currentIds.indexOf(entity.id), 1);
        }
    }

    for (let index = after.length - 1; index >= 0; index--) {
        const entity = after[index];
        if (beforeById.has(entity.id)) {
            continue;
        }
        const beforeId = after[index + 1]?.id ?? null;
        phases.adds.push(callbacks.add(clone(entity), beforeId));
        insertBeforeId(currentIds, entity.id, beforeId);
    }

    for (let index = 0; index < after.length; index++) {
        const targetId = after[index].id;
        if (currentIds[index] === targetId) {
            continue;
        }
        const beforeId = currentIds[index] ?? null;
        phases.moves.push(callbacks.move(targetId, beforeId));
        moveBeforeId(currentIds, targetId, beforeId);
    }

    for (const entity of after) {
        const previous = beforeById.get(entity.id);
        if (previous) {
            callbacks.patch(previous, entity);
        }
    }
};

const diffStages = (
    before: CanonicalClip,
    after: CanonicalClip,
    phases: CommandPhases,
): void =>
    diffEntityList(before.stages, after.stages, phases, {
        remove: (stageId) => ({
            type: "stage.remove",
            clipId: before.id,
            stageId,
        }),
        add: (stage, beforeStageId) => ({
            type: "stage.add",
            clipId: after.id,
            stage,
            beforeStageId,
        }),
        move: (stageId, beforeStageId) => ({
            type: "stage.move",
            clipId: after.id,
            stageId,
            beforeStageId,
        }),
        patch: (previous, next) => {
            if (
                previous.model !== next.model ||
                previous.modelProfileId !== next.modelProfileId
            ) {
                phases.patches.push({
                    type: "stage.retarget-model",
                    clipId: after.id,
                    stageId: next.id,
                    target: {
                        architectureId: after.architecture,
                        modelProfileId: next.modelProfileId,
                        model: next.model,
                    },
                });
            }
            const patch = changedPatch(previous, next, STAGE_PATCH_KEYS);
            if (hasPatch(patch)) {
                phases.patches.push({
                    type: "stage.patch",
                    clipId: after.id,
                    stageId: next.id,
                    patch,
                });
            }
        },
    });

const diffRefs = (
    before: CanonicalClip,
    after: CanonicalClip,
    phases: CommandPhases,
): void =>
    diffEntityList(before.refs, after.refs, phases, {
        remove: (refId) => ({
            type: "ref.remove",
            clipId: before.id,
            refId,
        }),
        add: (ref, beforeRefId) => ({
            type: "ref.add",
            clipId: after.id,
            ref,
            beforeRefId,
        }),
        move: (refId, beforeRefId) => ({
            type: "ref.move",
            clipId: after.id,
            refId,
            beforeRefId,
        }),
        patch: (previous, next) => {
            const patch = changedPatch(previous, next, REF_PATCH_KEYS);
            if (hasPatch(patch)) {
                phases.patches.push({
                    type: "ref.patch",
                    clipId: after.id,
                    refId: next.id,
                    patch,
                });
            }
        },
    });

const diffAudioSegments = (
    before: CanonicalClip,
    after: CanonicalClip,
    phases: CommandPhases,
): void =>
    diffEntityList(before.audioSegments, after.audioSegments, phases, {
        remove: (segmentId) => ({
            type: "audio-segment.remove",
            clipId: before.id,
            segmentId,
        }),
        add: (segment, beforeSegmentId) => ({
            type: "audio-segment.add",
            clipId: after.id,
            segment,
            beforeSegmentId,
        }),
        move: (segmentId, beforeSegmentId) => ({
            type: "audio-segment.move",
            clipId: after.id,
            segmentId,
            beforeSegmentId,
        }),
        patch: (previous, next) => {
            const patch = changedPatch(
                previous,
                next,
                AUDIO_SEGMENT_PATCH_KEYS,
            );
            if (hasPatch(patch)) {
                phases.patches.push({
                    type: "audio-segment.patch",
                    clipId: after.id,
                    segmentId: next.id,
                    patch,
                });
            }
        },
    });

const diffPromptWindows = (
    before: CanonicalClip,
    after: CanonicalClip,
    phases: CommandPhases,
): void =>
    diffEntityList(before.promptWindows, after.promptWindows, phases, {
        remove: (windowId) => ({
            type: "prompt-window.remove",
            clipId: before.id,
            windowId,
        }),
        add: (window, beforeWindowId) => ({
            type: "prompt-window.add",
            clipId: after.id,
            window,
            beforeWindowId,
        }),
        move: (windowId, beforeWindowId) => ({
            type: "prompt-window.move",
            clipId: after.id,
            windowId,
            beforeWindowId,
        }),
        patch: (previous, next) => {
            const patch = changedPatch(
                previous,
                next,
                PROMPT_WINDOW_PATCH_KEYS,
            );
            if (hasPatch(patch)) {
                phases.patches.push({
                    type: "prompt-window.patch",
                    clipId: after.id,
                    windowId: next.id,
                    patch,
                });
            }
        },
    });

const diffRetake = (
    before: CanonicalClip,
    after: CanonicalClip,
    phases: CommandPhases,
): void => {
    if (before.retake?.id !== after.retake?.id) {
        if (before.retake) {
            phases.removes.push({
                type: "retake.remove",
                clipId: before.id,
                retakeId: before.retake.id,
            });
        }
        if (after.retake) {
            phases.adds.push({
                type: "retake.add",
                clipId: after.id,
                retake: clone(after.retake),
            });
        }
        return;
    }
    if (!before.retake || !after.retake) {
        return;
    }
    const patch = changedPatch(before.retake, after.retake, RETAKE_PATCH_KEYS);
    if (hasPatch(patch)) {
        phases.patches.push({
            type: "retake.patch",
            clipId: after.id,
            retakeId: after.retake.id,
            patch,
        });
    }
};

const diffClipChildren = (
    before: CanonicalClip,
    after: CanonicalClip,
    phases: CommandPhases,
): void => {
    diffStages(before, after, phases);
    diffRefs(before, after, phases);
    diffAudioSegments(before, after, phases);
    diffPromptWindows(before, after, phases);
    diffRetake(before, after, phases);
};

const diffSpans = (
    before: CanonicalAudioTrack,
    after: CanonicalAudioTrack,
    phases: CommandPhases,
): void =>
    diffEntityList(before.spans, after.spans, phases, {
        remove: (spanId) => ({
            type: "audio-span.remove",
            trackId: before.id,
            spanId,
        }),
        add: (span, beforeSpanId) => ({
            type: "audio-span.add",
            trackId: after.id,
            span,
            beforeSpanId,
        }),
        move: (spanId, beforeSpanId) => ({
            type: "audio-span.move",
            trackId: after.id,
            spanId,
            beforeSpanId,
        }),
        patch: (previous, next) => {
            const patch = changedPatch(previous, next, AUDIO_SPAN_PATCH_KEYS);
            if (hasPatch(patch)) {
                phases.patches.push({
                    type: "audio-span.patch",
                    trackId: after.id,
                    spanId: next.id,
                    patch,
                });
            }
        },
    });

const diffClips = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
    phases: CommandPhases,
    context: DocumentDiffContext,
): void =>
    diffEntityList(before.clips, after.clips, phases, {
        remove: (clipId) => ({ type: "clip.remove", clipId }),
        add: (clip, beforeClipId) => ({
            type: "clip.add",
            clip,
            beforeClipId,
        }),
        move: (clipId, beforeClipId) => ({
            type: "clip.move",
            clipId,
            beforeClipId,
        }),
        patch: (previous, next) => {
            const changesEffectiveIdentity =
                previous.architecture !== next.architecture ||
                previous.modelProfileId !== next.modelProfileId;
            const previousIdentity = deriveClipArchitectureIdentity(
                previous,
                context.architectureCatalog,
            );
            const nextIdentity = deriveClipArchitectureIdentity(
                next,
                context.architectureCatalog,
            );
            if (changesEffectiveIdentity) {
                if (
                    !nextIdentity ||
                    nextIdentity.architectureId !== next.architecture ||
                    nextIdentity.modelProfileId !== next.modelProfileId
                ) {
                    throw new DocumentDiffError("architecture-invariant");
                }
            }
            const changesAuthoredArchitecture =
                previousIdentity?.authoredArchitectureId !== null &&
                previousIdentity?.authoredArchitectureId !== undefined &&
                nextIdentity?.authoredArchitectureId !== null &&
                nextIdentity?.authoredArchitectureId !== undefined &&
                previousIdentity.authoredArchitectureId !==
                    nextIdentity.authoredArchitectureId;
            if (
                changesEffectiveIdentity &&
                !changesAuthoredArchitecture &&
                (previousIdentity?.authoredArchitectureId == null ||
                    nextIdentity?.authoredArchitectureId == null ||
                    previousIdentity.authoredArchitectureId !==
                        nextIdentity.authoredArchitectureId)
            ) {
                // Source/skipped transitions may toggle only the effective
                // identity while retaining the same authored Stage 0. Empty
                // clips have no authoritative model target to convert from.
                throw new DocumentDiffError("architecture-invariant");
            }
            let diffBase = previous;
            if (changesAuthoredArchitecture) {
                const catalog = context.architectureCatalog;
                const sourceArchitectureId =
                    previousIdentity?.authoredArchitectureId;
                const targetStage = next.stages[0];
                const targetEntry = catalog?.entries.find(
                    (entry) => entry.value === targetStage?.model,
                );
                const targetDescriptor = catalog?.architectures.find(
                    (entry) => entry.id === targetEntry?.architectureId,
                );
                if (
                    !catalog ||
                    !sourceArchitectureId ||
                    !targetStage ||
                    !targetEntry?.architectureId ||
                    !targetEntry.modelProfileId ||
                    !targetDescriptor ||
                    targetEntry.architectureId !==
                        nextIdentity?.authoredArchitectureId ||
                    targetEntry.modelProfileId !== targetStage.modelProfileId
                ) {
                    throw new DocumentDiffError("architecture-invariant");
                }
                const target = {
                    architectureId: targetEntry.architectureId,
                    modelProfileId: targetEntry.modelProfileId,
                    model: targetEntry.value,
                    capabilities: clone(targetDescriptor.capabilities),
                };
                const requestedForCleanup = clone(next);
                requestedForCleanup.architecture = sourceArchitectureId;
                const requestedPlan = planArchitectureConversion(
                    requestedForCleanup,
                    target,
                    catalog,
                );
                const baselinePlan = planArchitectureConversion(
                    previous,
                    target,
                    catalog,
                );
                if (!requestedPlan || !baselinePlan) {
                    throw new DocumentDiffError("architecture-invariant");
                }

                // The requested whole-document state must already reflect all
                // destructive cleanup. Restore only valid per-stage models,
                // which conversion intentionally seeds from Stage 0.
                const cleanedRequested = requestedPlan.clip as CanonicalClip;
                if (cleanedRequested.stages.length === next.stages.length) {
                    for (let index = 0; index < next.stages.length; index++) {
                        cleanedRequested.stages[index].model =
                            next.stages[index].model;
                        cleanedRequested.stages[index].modelProfileId =
                            next.stages[index].modelProfileId;
                    }
                }
                if (
                    !reconcileClipArchitectureIdentity(
                        cleanedRequested,
                        catalog,
                    ) ||
                    !deepEqual(cleanedRequested, next)
                ) {
                    throw new DocumentDiffError("architecture-invariant");
                }

                const convertedBase = baselinePlan.clip as CanonicalClip;
                if (
                    !reconcileClipArchitectureIdentity(convertedBase, catalog)
                ) {
                    throw new DocumentDiffError("architecture-invariant");
                }
                phases.conversions.push({
                    type: "clip.convert-architecture",
                    clipId: next.id,
                    target,
                });
                diffBase = convertedBase;
            }

            const patch = changedPatch(diffBase, next, CLIP_PATCH_KEYS);
            if (hasPatch(patch)) {
                phases.patches.push({
                    type: "clip.patch",
                    clipId: next.id,
                    patch,
                });
            }
            diffClipChildren(diffBase, next, phases);
        },
    });

const diffAudioTracks = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
    phases: CommandPhases,
): void =>
    diffEntityList(before.audioTracks, after.audioTracks, phases, {
        remove: (trackId) => ({ type: "audio-track.remove", trackId }),
        add: (track, beforeTrackId) => ({
            type: "audio-track.add",
            track,
            beforeTrackId,
        }),
        move: (trackId, beforeTrackId) => ({
            type: "audio-track.move",
            trackId,
            beforeTrackId,
        }),
        patch: (previous, next) => {
            const patch = changedPatch(previous, next, AUDIO_TRACK_PATCH_KEYS);
            if (hasPatch(patch)) {
                phases.patches.push({
                    type: "audio-track.patch",
                    trackId: next.id,
                    patch,
                });
            }
            diffSpans(previous, next, phases);
        },
    });

/**
 * Produces one atomic, stable-ID batch that transforms `before` into `after`.
 * It never falls back to whole-document replacement.
 */
export const diffDocuments = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
    context: DocumentDiffContext = { architectureCatalog: null },
): DocumentBatchCommand => {
    validateDocumentIds(before);
    validateDocumentIds(after);

    const phases: CommandPhases = {
        conversions: [],
        removes: [],
        adds: [],
        moves: [],
        patches: [],
    };
    const rootPatch = changedPatch(before, after, ROOT_PATCH_KEYS);
    diffClips(before, after, phases, context);
    if (phases.conversions.length > 0) {
        const forcedFinalClips = clone(after.clips);
        forceCrossArchitectureCutsForConversion(forcedFinalClips);
        if (
            forcedFinalClips.some(
                (clip, index) =>
                    clip.boundaryOut !== after.clips[index]?.boundaryOut,
            )
        ) {
            throw new DocumentDiffError("architecture-invariant");
        }

        // Each conversion applies its cut policy immediately. Reassert final
        // requested boundaries after all conversions so an atomic multi-clip
        // conversion is judged by its final architecture adjacency, not an
        // intermediate ordering artifact.
        for (const clip of after.clips) {
            phases.patches.push({
                type: "clip.patch",
                clipId: clip.id,
                patch: { boundaryOut: clip.boundaryOut },
            });
        }
    }
    diffAudioTracks(before, after, phases);

    return {
        type: "batch",
        commands: [
            ...(hasPatch(rootPatch)
                ? [{ type: "root.patch", patch: rootPatch } as const]
                : []),
            ...phases.conversions,
            ...phases.removes,
            ...phases.adds,
            ...phases.moves,
            ...phases.patches,
        ],
    };
};
