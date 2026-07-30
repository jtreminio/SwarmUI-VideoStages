import { activeStageCount } from "../clipSemantics";
import type { Clip } from "../types";
import { modelCatalogEntry } from "./catalogQueries";
import { NONE_ARCHITECTURE_ID } from "./none/identity";
import type { ArchitectureModelCatalog } from "./types";

export interface StageModelIdentity {
    architectureId: string;
    modelProfileId: string;
    compatibilityClassId: string;
}

export interface ClipArchitectureIdentity {
    architectureId: string;
    modelProfileId: string;
    authoredArchitectureId: string | null;
    authoredModelProfileId: string | null;
}

export const modelIdentityFromCatalog = (
    catalog: ArchitectureModelCatalog | null,
    model: string,
): StageModelIdentity | null => {
    if (!catalog) return null;
    const entry = modelCatalogEntry(catalog, model);
    if (
        !entry?.architectureId ||
        !entry.modelProfileId ||
        !entry.compatibilityClassId
    ) {
        return null;
    }
    return {
        architectureId: entry.architectureId,
        modelProfileId: entry.modelProfileId,
        compatibilityClassId: entry.compatibilityClassId,
    };
};

/**
 * Resolves the architecture that may execute frontend behavior for a clip.
 * Persisted architecture/profile fields are repair hints only and never make
 * an unresolved Stage-0 model executable.
 */
export const resolvedClipArchitectureId = (
    clip: Clip,
    catalog: ArchitectureModelCatalog | null,
): string | null => {
    if (clip.sourceVideo !== null && activeStageCount(clip) === 0) {
        return NONE_ARCHITECTURE_ID;
    }
    const stageZeroModel = clip.stages[0]?.model;
    return stageZeroModel
        ? (modelIdentityFromCatalog(catalog, stageZeroModel)?.architectureId ??
              null)
        : null;
};

/**
 * Pure Stage-0 identity derivation shared by commands, whole-document diffs,
 * and UI preview mutations.
 */
export const deriveClipArchitectureIdentity = (
    clip: Clip,
    catalog: ArchitectureModelCatalog | null,
): ClipArchitectureIdentity | null => {
    if (!catalog) return null;
    const identities = clip.stages.map((stage) => ({
        stage,
        identity: modelIdentityFromCatalog(catalog, stage.model),
    }));
    if (identities.some(({ identity }) => !identity)) {
        return null;
    }
    const authored = identities[0]?.identity ?? null;
    if (
        authored &&
        identities.some(
            ({ identity }) =>
                identity?.architectureId !== authored.architectureId ||
                identity.compatibilityClassId !== authored.compatibilityClassId,
        )
    ) {
        return null;
    }
    const authoredIdentity = {
        authoredArchitectureId: authored?.architectureId ?? null,
        authoredModelProfileId: authored?.modelProfileId ?? null,
    };
    if (clip.sourceVideo !== null && activeStageCount(clip) === 0) {
        return {
            architectureId: NONE_ARCHITECTURE_ID,
            modelProfileId: NONE_ARCHITECTURE_ID,
            ...authoredIdentity,
        };
    }
    if (authored) {
        return {
            architectureId: authored.architectureId,
            modelProfileId: authored.modelProfileId,
            ...authoredIdentity,
        };
    }
    if (
        clip.architectureHint === NONE_ARCHITECTURE_ID &&
        clip.modelProfileId === NONE_ARCHITECTURE_ID
    ) {
        return {
            architectureId: NONE_ARCHITECTURE_ID,
            modelProfileId: NONE_ARCHITECTURE_ID,
            ...authoredIdentity,
        };
    }
    const validEmptyIdentity = catalog.architectures.some(
        (architecture) => architecture.id === clip.architectureHint,
    );
    return validEmptyIdentity
        ? {
              architectureId: clip.architectureHint,
              modelProfileId: clip.modelProfileId,
              ...authoredIdentity,
          }
        : null;
};

export const reconcileClipArchitectureIdentity = (
    clip: Clip,
    catalog: ArchitectureModelCatalog | null,
): boolean => {
    const identity = deriveClipArchitectureIdentity(clip, catalog);
    if (!identity) return false;
    clip.architectureHint = identity.architectureId;
    clip.modelProfileId = identity.modelProfileId;
    return true;
};
