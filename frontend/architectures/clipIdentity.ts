import { activeStageCount } from "../clipSemantics";
import type { Clip } from "../types";
import { architectureDescriptor, modelCatalogEntry } from "./catalogQueries";
import { NONE_ARCHITECTURE_ID } from "./none/identity";
import type { ArchitectureModelCatalog } from "./types";

export interface StageModelIdentity {
    architectureId: string;
    modelProfileId: string;
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
        !architectureDescriptor(catalog, entry.architectureId)?.profiles.some(
            (profile) => profile.id === entry.modelProfileId,
        )
    ) {
        return null;
    }
    return {
        architectureId: entry.architectureId,
        modelProfileId: entry.modelProfileId,
    };
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
    if (
        identities.some(
            ({ stage, identity }) =>
                !identity || stage.modelProfileId !== identity.modelProfileId,
        )
    ) {
        return null;
    }
    const authored = identities[0]?.identity ?? null;
    if (
        authored &&
        identities.some(
            ({ identity }) =>
                identity?.architectureId !== authored.architectureId,
        )
    ) {
        return null;
    }
    const descriptor = authored
        ? architectureDescriptor(catalog, authored.architectureId)
        : null;
    if (
        clip.stages.length > 1 &&
        !descriptor?.capabilities.architecture.includes("multi-stage")
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
        clip.architecture === NONE_ARCHITECTURE_ID &&
        clip.modelProfileId === NONE_ARCHITECTURE_ID
    ) {
        return {
            architectureId: NONE_ARCHITECTURE_ID,
            modelProfileId: NONE_ARCHITECTURE_ID,
            ...authoredIdentity,
        };
    }
    const validEmptyIdentity =
        catalog.architectures
            .find((architecture) => architecture.id === clip.architecture)
            ?.profiles.some((profile) => profile.id === clip.modelProfileId) ??
        false;
    return validEmptyIdentity
        ? {
              architectureId: clip.architecture,
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
    clip.architecture = identity.architectureId;
    clip.modelProfileId = identity.modelProfileId;
    return true;
};
