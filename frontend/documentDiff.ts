import { reconcileClipArchitectureIncomingIcLoraDrives } from "./architectures/behaviorRegistry";
import {
    architectureDescriptor,
    modelCatalogEntry,
} from "./architectures/catalogQueries";
import {
    deriveClipArchitectureIdentity,
    modelIdentityFromCatalog,
    reconcileClipArchitectureIdentity,
} from "./architectures/clipIdentity";
import { planArchitectureConversion } from "./architectures/conversion/plan";
import { NONE_ARCHITECTURE_ID } from "./architectures/none/identity";
import { forceCrossArchitectureCutsForConversion } from "./architectures/policy/boundaryPolicy";
import type { ArchitectureRetargetPlan } from "./architectures/types";
import type {
    CommandFailure,
    DocumentCommand,
    DocumentCommandContext,
} from "./documentCommands";
import {
    LIST_ENTITIES,
    type ListEntityDescriptor,
    OWNER_ID_FIELD,
    RETAKE_PATCH_KEYS,
    ROOT_PATCH_KEYS,
} from "./documentCommands/listEntities";
import { ownedIds } from "./identity";
import type { CanonicalClip, CanonicalVideoStagesConfig } from "./types";

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
    preConversions: DocumentCommand[];
    conversions: DocumentCommand[];
    removes: DocumentCommand[];
    adds: DocumentCommand[];
    moves: DocumentCommand[];
    patches: DocumentCommand[];
}

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
    ...document.clips.flatMap(ownedIds),
    ...document.audioTracks.flatMap(ownedIds),
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

const listCommand = <TOwner, TEntity extends { id: string }>(
    descriptor: ListEntityDescriptor<TOwner, TEntity>,
    ownerId: string | null,
    suffix: "add" | "remove" | "move" | "patch",
    fields: Record<string, unknown>,
): DocumentCommand => {
    const ownerField = OWNER_ID_FIELD[descriptor.owner];
    return {
        type: `${descriptor.prefix}.${suffix}`,
        ...(ownerField === null ? {} : { [ownerField]: ownerId }),
        ...fields,
    } as unknown as DocumentCommand;
};

const emitPatch = <TOwner, TEntity extends { id: string }>(
    descriptor: ListEntityDescriptor<TOwner, TEntity>,
    ownerId: string | null,
    previous: TEntity,
    next: TEntity,
    phases: CommandPhases,
): void => {
    const patch = changedPatch(previous, next, descriptor.patchKeys);
    if (hasPatch(patch)) {
        phases.patches.push(
            listCommand(descriptor, ownerId, "patch", {
                [descriptor.idField]: next.id,
                patch,
            }),
        );
    }
};

/**
 * Turns one owner's before/after collection into remove/add/move/patch
 * commands, phased so the batch stays applicable in order.
 */
const diffList = <TOwner, TEntity extends { id: string }>(
    descriptor: ListEntityDescriptor<TOwner, TEntity>,
    ownerId: string | null,
    beforeOwner: TOwner,
    afterOwner: TOwner,
    phases: CommandPhases,
    patchEntity: (previous: TEntity, next: TEntity) => void = (
        previous,
        next,
    ) => emitPatch(descriptor, ownerId, previous, next, phases),
): void => {
    const before = descriptor.collection(beforeOwner);
    const after = descriptor.collection(afterOwner);
    const beforeById = new Map(before.map((entity) => [entity.id, entity]));
    const afterIds = new Set(after.map((entity) => entity.id));
    const currentIds = before.map((entity) => entity.id);

    for (const entity of before) {
        if (!afterIds.has(entity.id)) {
            phases.removes.push(
                listCommand(descriptor, ownerId, "remove", {
                    [descriptor.idField]: entity.id,
                }),
            );
            currentIds.splice(currentIds.indexOf(entity.id), 1);
        }
    }

    for (let index = after.length - 1; index >= 0; index--) {
        const entity = after[index];
        if (beforeById.has(entity.id)) {
            continue;
        }
        const beforeId = after[index + 1]?.id ?? null;
        phases.adds.push(
            listCommand(descriptor, ownerId, "add", {
                [descriptor.entityField]: clone(entity),
                [descriptor.beforeIdField]: beforeId,
            }),
        );
        insertBeforeId(currentIds, entity.id, beforeId);
    }

    for (let index = 0; index < after.length; index++) {
        const targetId = after[index].id;
        if (currentIds[index] === targetId) {
            continue;
        }
        const beforeId = currentIds[index] ?? null;
        phases.moves.push(
            listCommand(descriptor, ownerId, "move", {
                [descriptor.idField]: targetId,
                [descriptor.beforeIdField]: beforeId,
            }),
        );
        moveBeforeId(currentIds, targetId, beforeId);
    }

    for (const entity of after) {
        const previous = beforeById.get(entity.id);
        if (previous) {
            patchEntity(previous, entity);
        }
    }
};

const diffStages = (
    before: CanonicalClip,
    after: CanonicalClip,
    phases: CommandPhases,
    context: DocumentCommandContext,
): void =>
    diffList(
        LIST_ENTITIES.stage,
        after.id,
        before,
        after,
        phases,
        (previous, next) => {
            if (
                previous.model !== next.model ||
                previous.modelProfileId !== next.modelProfileId
            ) {
                const targetEntry = modelCatalogEntry(
                    context.architectureCatalog,
                    next.model,
                );
                if (
                    !targetEntry?.architectureId ||
                    !targetEntry.modelProfileId
                ) {
                    throw new DocumentDiffError("architecture-invariant");
                }
                phases.patches.push({
                    type: "stage.retarget-model",
                    clipId: after.id,
                    stageId: next.id,
                    target: {
                        architectureId: targetEntry.architectureId,
                        modelProfileId: targetEntry.modelProfileId,
                        model: targetEntry.value,
                    },
                });
            }
            emitPatch(LIST_ENTITIES.stage, after.id, previous, next, phases);
        },
    );

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
    context: DocumentCommandContext,
): void => {
    diffStages(before, after, phases, context);
    diffList(LIST_ENTITIES.ref, after.id, before, after, phases);
    diffList(LIST_ENTITIES.clipReference, after.id, before, after, phases);
    diffList(LIST_ENTITIES.promptWindow, after.id, before, after, phases);
    diffRetake(before, after, phases);
};

/**
 * Re-derives the architecture conversion a whole-document save implies, and
 * returns the baseline the rest of the clip diff must be taken against: the
 * converted clip when Stage 0 changed architecture, otherwise `previous`.
 */
const clipDiffBase = (
    previous: CanonicalClip,
    next: CanonicalClip,
    phases: CommandPhases,
    context: DocumentCommandContext,
): CanonicalClip => {
    const changesEffectiveIdentity =
        previous.architectureHint !== next.architectureHint ||
        previous.modelProfileId !== next.modelProfileId;
    const previousIdentity = deriveClipArchitectureIdentity(
        previous,
        context.architectureCatalog,
    );
    const nextIdentity = deriveClipArchitectureIdentity(
        next,
        context.architectureCatalog,
    );
    const previousStageZeroIdentity = modelIdentityFromCatalog(
        context.architectureCatalog,
        previous.stages[0]?.model ?? "",
    );
    const nextStageZeroIdentity = modelIdentityFromCatalog(
        context.architectureCatalog,
        next.stages[0]?.model ?? "",
    );
    const repairsUnresolvedStageZero =
        previous.stages[0]?.model !== next.stages[0]?.model &&
        previousStageZeroIdentity === null &&
        nextStageZeroIdentity !== null;
    if (changesEffectiveIdentity) {
        if (
            !nextIdentity ||
            nextIdentity.architectureId !== next.architectureHint ||
            nextIdentity.modelProfileId !== next.modelProfileId
        ) {
            throw new DocumentDiffError("architecture-invariant");
        }
    }
    const changesAuthoredArchitecture =
        repairsUnresolvedStageZero ||
        (previousIdentity?.authoredArchitectureId !== null &&
            previousIdentity?.authoredArchitectureId !== undefined &&
            nextIdentity?.authoredArchitectureId !== null &&
            nextIdentity?.authoredArchitectureId !== undefined &&
            previousIdentity.authoredArchitectureId !==
                nextIdentity.authoredArchitectureId);
    // Deleting the last stage of a initVideoClip clip leaves a valid source-only clip: the documented
    // `none` architecture case. It legitimately has no authored Stage 0 and therefore no next
    // authored architecture to convert to, so the stage removals plus the clip's own architecture
    // patch describe the change completely. An empty clip that is NOT initVideoClip still has no
    // authoritative model target and keeps failing below.
    const nextIsSourceOnlyClip =
        next.stages.length === 0 &&
        next.initVideo !== null &&
        nextIdentity?.authoredArchitectureId == null &&
        nextIdentity?.architectureId === NONE_ARCHITECTURE_ID;
    if (
        changesEffectiveIdentity &&
        !changesAuthoredArchitecture &&
        !nextIsSourceOnlyClip &&
        (previousIdentity?.authoredArchitectureId == null ||
            nextIdentity?.authoredArchitectureId == null ||
            previousIdentity.authoredArchitectureId !==
                nextIdentity.authoredArchitectureId)
    ) {
        // Source/skipped transitions may toggle only the effective identity
        // while retaining the same authored Stage 0. Empty clips have no
        // authoritative model target to convert from.
        throw new DocumentDiffError("architecture-invariant");
    }
    if (!changesAuthoredArchitecture) {
        return previous;
    }

    const catalog = context.architectureCatalog;
    const targetStage = next.stages[0];
    const targetEntry = modelCatalogEntry(catalog, targetStage?.model);
    const targetDescriptor = architectureDescriptor(
        catalog,
        targetEntry?.architectureId,
    );
    if (
        !catalog ||
        !targetStage ||
        !targetEntry?.architectureId ||
        !targetEntry.modelProfileId ||
        !targetDescriptor ||
        targetEntry.architectureId !== nextIdentity?.authoredArchitectureId
    ) {
        throw new DocumentDiffError("architecture-invariant");
    }
    const target: ArchitectureRetargetPlan = {
        architectureId: targetEntry.architectureId,
        modelProfileId: targetEntry.modelProfileId,
        model: targetEntry.value,
    };
    // Root input and active-stage topology determine whether the target model
    // can enter the clip. Apply those non-identity edits before conversion so
    // the reducer validates the same role that the final atomic document uses.
    const conversionSource = clone(previous);
    if (!deepEqual(previous.initVideo, next.initVideo)) {
        phases.preConversions.push({
            type: "clip.patch",
            clipId: next.id,
            patch: { initVideo: clone(next.initVideo) },
        });
        conversionSource.initVideo = clone(next.initVideo);
    }
    const nextStagesById = new Map(
        next.stages.map((stage) => [stage.id, stage]),
    );
    const nextStageIds = new Set(nextStagesById.keys());
    for (const stage of conversionSource.stages) {
        if (!nextStageIds.has(stage.id)) {
            phases.preConversions.push({
                type: "stage.remove",
                clipId: next.id,
                stageId: stage.id,
            });
        }
    }
    conversionSource.stages = conversionSource.stages.filter((stage) =>
        nextStageIds.has(stage.id),
    );
    for (const stage of conversionSource.stages) {
        const nextStage = nextStagesById.get(stage.id);
        if (nextStage && stage.skipped !== nextStage.skipped) {
            phases.preConversions.push({
                type: "stage.patch",
                clipId: next.id,
                stageId: stage.id,
                patch: { skipped: nextStage.skipped },
            });
            stage.skipped = nextStage.skipped;
        }
    }
    const baselinePlan = planArchitectureConversion(
        conversionSource,
        target,
        catalog,
    );
    if (!baselinePlan) {
        throw new DocumentDiffError("architecture-invariant");
    }

    // The requested whole-document state must already reflect all destructive
    // cleanup derived from the previous payload owner. The requested state
    // already carries its final models and other dormant authored values.
    const cleanedRequested = clone(next);
    if (
        !reconcileClipArchitectureIdentity(cleanedRequested, catalog) ||
        !deepEqual(cleanedRequested, next)
    ) {
        throw new DocumentDiffError("architecture-invariant");
    }

    const convertedBase = baselinePlan as CanonicalClip;
    if (!reconcileClipArchitectureIdentity(convertedBase, catalog)) {
        throw new DocumentDiffError("architecture-invariant");
    }
    phases.conversions.push({
        type: "clip.convert-architecture",
        clipId: next.id,
        target,
    });
    return convertedBase;
};

const diffClips = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
    phases: CommandPhases,
    context: DocumentCommandContext,
): void =>
    diffList(
        LIST_ENTITIES.clip,
        null,
        before,
        after,
        phases,
        (previous, next) => {
            const diffBase = clipDiffBase(previous, next, phases, context);
            emitPatch(LIST_ENTITIES.clip, null, diffBase, next, phases);
            diffClipChildren(diffBase, next, phases, context);
        },
    );

const diffAudioTracks = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
    phases: CommandPhases,
): void =>
    diffList(
        LIST_ENTITIES.audioTrack,
        null,
        before,
        after,
        phases,
        (previous, next) => {
            emitPatch(LIST_ENTITIES.audioTrack, null, previous, next, phases);
            diffList(LIST_ENTITIES.audioSpan, next.id, previous, next, phases);
        },
    );

/**
 * Produces one atomic, stable-ID batch that transforms `before` into `after`.
 * It never falls back to whole-document replacement.
 */
export const diffDocuments = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
    context: DocumentCommandContext = { architectureCatalog: null },
): DocumentBatchCommand => {
    validateDocumentIds(before);
    validateDocumentIds(after);

    const phases: CommandPhases = {
        preConversions: [],
        conversions: [],
        removes: [],
        adds: [],
        moves: [],
        patches: [],
    };
    const rootPatch = changedPatch(before, after, ROOT_PATCH_KEYS);
    diffClips(before, after, phases, context);
    if (phases.conversions.length > 0) {
        const reconciledAfter = clone(after);
        for (const conversion of phases.conversions) {
            if (conversion.type !== "clip.convert-architecture") continue;
            const clipIdx = reconciledAfter.clips.findIndex(
                (clip) => clip.id === conversion.clipId,
            );
            reconcileClipArchitectureIncomingIcLoraDrives(
                reconciledAfter.clips,
                clipIdx,
                context.generatedEntryMode ?? "text-to-video",
                context.architectureCatalog,
            );
        }
        if (!deepEqual(reconciledAfter, after)) {
            throw new DocumentDiffError("architecture-invariant");
        }
        const forcedFinalClips = clone(after.clips);
        forceCrossArchitectureCutsForConversion(
            forcedFinalClips,
            context.architectureCatalog,
        );
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
        // Conversion repairs graph-relative Incoming media immediately, while
        // an atomic batch may still have clip/stage topology edits to apply.
        // Reassert the final IC-LoRA state only after it passed the final-graph
        // reconciliation above, so replay cannot depend on intermediate order.
        const convertedClipIds = new Set(
            phases.conversions.flatMap((conversion) =>
                conversion.type === "clip.convert-architecture"
                    ? [conversion.clipId]
                    : [],
            ),
        );
        for (const clip of after.clips) {
            if (!convertedClipIds.has(clip.id)) continue;
            phases.patches.push({
                type: "clip.patch",
                clipId: clip.id,
                patch: { icLoras: clone(clip.icLoras) },
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
            ...phases.preConversions,
            ...phases.conversions,
            ...phases.removes,
            ...phases.adds,
            ...phases.moves,
            ...phases.patches,
        ],
    };
};
