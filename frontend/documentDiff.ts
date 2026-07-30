import {
    architectureDescriptor,
    modelCatalogEntry,
} from "./architectures/catalogQueries";
import {
    deriveClipArchitectureIdentity,
    reconcileClipArchitectureIdentity,
} from "./architectures/clipIdentity";
import type { GeneratedEntryMode } from "./architectures/conversion/entryModePolicy";
import { planArchitectureConversion } from "./architectures/conversion/plan";
import { NONE_ARCHITECTURE_ID } from "./architectures/none/identity";
import { forceCrossArchitectureCutsForConversion } from "./architectures/policy/boundaryPolicy";
import type { ArchitectureModelCatalog } from "./architectures/types";
import type { CommandFailure, DocumentCommand } from "./documentCommands";
import {
    LIST_ENTITIES,
    type ListEntityDescriptor,
    OWNER_ID_FIELD,
    RETAKE_PATCH_KEYS,
    ROOT_PATCH_KEYS,
} from "./documentCommands/listEntities";
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
    conversions: DocumentCommand[];
    removes: DocumentCommand[];
    adds: DocumentCommand[];
    moves: DocumentCommand[];
    patches: DocumentCommand[];
}

export interface DocumentDiffContext {
    architectureCatalog: ArchitectureModelCatalog | null;
    generatedEntryMode?: GeneratedEntryMode;
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
    ...document.clips.flatMap((clip) => [
        clip.id,
        ...clip.stages.map((stage) => stage.id),
        ...clip.refs.map((ref) => ref.id),
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
): void => {
    diffStages(before, after, phases);
    diffList(LIST_ENTITIES.ref, after.id, before, after, phases);
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
    context: DocumentDiffContext,
): CanonicalClip => {
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
    // Deleting the last stage of a sourced clip leaves a valid source-only clip: the documented
    // `none` architecture case. It legitimately has no authored Stage 0 and therefore no next
    // authored architecture to convert to, so the stage removals plus the clip's own architecture
    // patch describe the change completely. An empty clip that is NOT sourced still has no
    // authoritative model target and keeps failing below.
    const nextIsSourceOnlyClip =
        next.stages.length === 0 &&
        next.sourceVideo !== null &&
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
    const sourceArchitectureId = previousIdentity?.authoredArchitectureId;
    const targetStage = next.stages[0];
    const targetEntry = modelCatalogEntry(catalog, targetStage?.model);
    const targetDescriptor = architectureDescriptor(
        catalog,
        targetEntry?.architectureId,
    );
    if (
        !catalog ||
        !sourceArchitectureId ||
        !targetStage ||
        !targetEntry?.architectureId ||
        !targetEntry.modelProfileId ||
        !targetDescriptor ||
        targetEntry.architectureId !== nextIdentity?.authoredArchitectureId
    ) {
        throw new DocumentDiffError("architecture-invariant");
    }
    const target = {
        architectureId: targetEntry.architectureId,
        modelProfileId: targetEntry.modelProfileId,
        model: targetEntry.value,
        capabilities: clone(targetDescriptor.capabilities),
        entryModes: clone(targetEntry.entryModes),
    };
    const requestedForCleanup = clone(next);
    requestedForCleanup.architecture = sourceArchitectureId;
    const requestedPlan = planArchitectureConversion(
        requestedForCleanup,
        target,
        catalog,
        context.generatedEntryMode ?? "text-to-video",
    );
    const baselinePlan = planArchitectureConversion(
        previous,
        target,
        catalog,
        context.generatedEntryMode ?? "text-to-video",
    );
    if (!requestedPlan || !baselinePlan) {
        throw new DocumentDiffError("architecture-invariant");
    }

    // The requested whole-document state must already reflect all destructive
    // cleanup. Restore only valid per-stage models, which conversion
    // intentionally seeds from Stage 0.
    const cleanedRequested = requestedPlan.clip as CanonicalClip;
    if (cleanedRequested.stages.length === next.stages.length) {
        for (let index = 0; index < next.stages.length; index++) {
            cleanedRequested.stages[index].model = next.stages[index].model;
            cleanedRequested.stages[index].modelProfileId =
                next.stages[index].modelProfileId;
        }
    }
    if (
        !reconcileClipArchitectureIdentity(cleanedRequested, catalog) ||
        !deepEqual(cleanedRequested, next)
    ) {
        throw new DocumentDiffError("architecture-invariant");
    }

    const convertedBase = baselinePlan.clip as CanonicalClip;
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
    context: DocumentDiffContext,
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
            diffClipChildren(diffBase, next, phases);
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
