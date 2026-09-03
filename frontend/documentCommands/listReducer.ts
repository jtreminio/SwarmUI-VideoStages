import { reconcileClipArchitectureIdentity } from "../architectures/clipIdentity";
import { createCapabilityViewResolver } from "../architectures/policy";
import { clipReferenceIsUsable } from "../clipReferenceSource";
import type { CanonicalAuthoringDocument, CanonicalClip } from "../types";
import {
    addBefore,
    clone,
    failure,
    findClip,
    findTrack,
    hasOwn,
    invalidNewEntity,
    moveBefore,
    patchById,
    removeById,
    success,
} from "./helpers";
import { type LIST_ENTITIES, OWNER_ID_FIELD } from "./listEntities";
import type { DocumentCommandContext, DocumentCommandResult } from "./types";

type ListDescriptor = (typeof LIST_ENTITIES)[keyof typeof LIST_ENTITIES];
type Entity = { id: string };

export type ListOperation = "add" | "remove" | "move" | "patch";

/**
 * Applies one `${prefix}.${operation}` command using only the shared list
 * descriptor: it resolves the owner, addresses the collection, and adopts the
 * mutation. Entity-specific rules stay in the reducer's own cases.
 */
export const applyListCommand = (
    document: CanonicalAuthoringDocument,
    descriptor: ListDescriptor,
    operation: ListOperation,
    command: object,
    context: DocumentCommandContext,
): DocumentCommandResult => {
    const fields = command as Record<string, unknown>;
    const ownerField = OWNER_ID_FIELD[descriptor.owner];
    const ownerId = ownerField === null ? "" : (fields[ownerField] as string);
    const owner =
        descriptor.owner === "document"
            ? document
            : descriptor.owner === "clip"
              ? findClip(document, ownerId)
              : findTrack(document, ownerId);
    if (!owner) {
        return failure(document, "missing-target");
    }

    // Stage edits can change the owning clip's identity, so they mutate a
    // candidate clip that is adopted only once reconciliation succeeds.
    const target = descriptor.reconcilesClipIdentity
        ? clone(owner as CanonicalClip)
        : owner;
    const items = (descriptor.collection as (owner: unknown) => Entity[])(
        target,
    );
    const id = fields[descriptor.idField] as string;
    const affectedId =
        operation === "add"
            ? (fields[descriptor.entityField] as Entity).id
            : id;
    const wasUsableImage =
        descriptor.reconcilesCharacterRefineReferences &&
        (owner as CanonicalClip).references.some(
            (reference) =>
                reference.id === affectedId &&
                reference.kind === "image" &&
                clipReferenceIsUsable(reference),
        );
    const beforeId = (fields[descriptor.beforeIdField] ?? null) as
        | string
        | null;
    let mutated: boolean;
    switch (operation) {
        case "add": {
            const entity = fields[descriptor.entityField] as Entity;
            const invalid = invalidNewEntity(
                document,
                entity as Parameters<typeof invalidNewEntity>[1],
            );
            if (invalid) {
                return invalid;
            }
            mutated = addBefore(items, clone(entity), beforeId);
            break;
        }
        case "remove":
            mutated = removeById(items, id);
            break;
        case "move":
            mutated = moveBefore(items, id, beforeId);
            break;
        case "patch": {
            const patch = fields.patch as Record<string, unknown>;
            // Reserved keys belong to a dedicated command (model retargeting,
            // child collections); a patch may never smuggle them in.
            if (
                descriptor.reservedKeys.some(
                    (key) => key !== "id" && hasOwn(patch, key),
                )
            ) {
                return failure(document, "architecture-invariant");
            }
            mutated = patchById(items, id, patch);
            break;
        }
    }
    if (!mutated) {
        return failure(document, "missing-target");
    }
    if (descriptor.reconcilesCharacterRefineReferences) {
        const supportsAppearanceReferences =
            context.architectureCatalog !== null &&
            createCapabilityViewResolver(context.architectureCatalog)
                .forClip(owner as CanonicalClip)
                .decision("characterRefineAppearanceReferences").supported;
        const reference = (owner as CanonicalClip).references.find(
            (candidate) => candidate.id === affectedId,
        );
        const usableImage =
            operation !== "remove" &&
            reference !== undefined &&
            reference.kind === "image" &&
            clipReferenceIsUsable(reference);
        for (const stage of (owner as CanonicalClip).stages) {
            if (!stage.characterRefine) continue;
            if (
                supportsAppearanceReferences &&
                usableImage &&
                !wasUsableImage
            ) {
                if (
                    !stage.characterRefine.pictureReferenceIds.includes(
                        affectedId,
                    )
                ) {
                    stage.characterRefine.pictureReferenceIds.push(affectedId);
                }
            } else if (!usableImage) {
                stage.characterRefine.pictureReferenceIds =
                    stage.characterRefine.pictureReferenceIds.filter(
                        (referenceId) => referenceId !== affectedId,
                    );
            }
            const hasUsableImage = (owner as CanonicalClip).references.some(
                (candidate) =>
                    candidate.kind === "image" &&
                    clipReferenceIsUsable(candidate),
            );
            if (!hasUsableImage) {
                stage.characterRefine.verifyIdentity = false;
            }
        }
    }
    if (descriptor.reconcilesClipIdentity) {
        const candidate = target as CanonicalClip;
        if (
            !reconcileClipArchitectureIdentity(
                candidate,
                context.architectureCatalog,
            )
        ) {
            return failure(document, "architecture-invariant");
        }
        document.clips[document.clips.indexOf(owner as CanonicalClip)] =
            candidate;
    }
    return success(document);
};
