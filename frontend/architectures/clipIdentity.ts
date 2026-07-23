import type { Clip } from "../types";
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
    const entry = catalog.entries.find(
        (candidate) => candidate.value === model,
    );
    if (
        !entry?.architectureId ||
        !entry.modelProfileId ||
        !catalog.architectures.some(
            (architecture) =>
                architecture.id === entry.architectureId &&
                architecture.profiles.some(
                    (profile) => profile.id === entry.modelProfileId,
                ),
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
        ? catalog.architectures.find(
              (candidate) => candidate.id === authored.architectureId,
          )
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
    if (
        clip.sourceVideo !== null &&
        clip.stages.every((stage) => stage.skipped)
    ) {
        return {
            architectureId: "none",
            modelProfileId: "none",
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
    if (clip.architecture === "none" && clip.modelProfileId === "none") {
        return {
            architectureId: "none",
            modelProfileId: "none",
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
