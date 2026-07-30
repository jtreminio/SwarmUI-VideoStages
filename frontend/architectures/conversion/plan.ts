import type { Clip } from "../../types";
import { architectureDescriptor, modelCatalogEntry } from "../catalogQueries";
import { modelIdentityFromCatalog } from "../clipIdentity";
import { NONE_ARCHITECTURE_ID } from "../none/identity";
import type {
    ArchitectureModelCatalog,
    ArchitectureRetargetPlan,
} from "../types";
import {
    type GeneratedEntryMode,
    modelSupportsAllActiveStageEntries,
} from "./entryModePolicy";

export interface ArchitectureConversionPlan {
    /** The complete converted clip; the source clip is never mutated. */
    clip: Clip;
    /** User-facing summary produced from the same decisions as `clip`. */
    removals: string[];
    /** Stable IDs removed by the conversion, for selection invalidation. */
    removedEntityIds: string[];
    selectionAffected: boolean;
}

type ResolvedArchitectureRetarget = ArchitectureRetargetPlan;

/**
 * Resolves a caller-supplied target against model and architecture facts. The
 * profile id is a migration hint; caller-supplied capability arrays never
 * self-authorize.
 */
export const resolveArchitectureRetarget = (
    requested: Pick<
        ArchitectureRetargetPlan,
        "architectureId" | "modelProfileId" | "model"
    >,
    catalog: ArchitectureModelCatalog | null,
): ResolvedArchitectureRetarget | null => {
    if (!catalog) {
        return null;
    }
    const model = modelCatalogEntry(catalog, requested.model);
    if (
        !model?.architectureId ||
        !model.modelProfileId ||
        model.architectureId !== requested.architectureId
    ) {
        return null;
    }
    const descriptor = architectureDescriptor(catalog, model.architectureId);
    if (!descriptor) {
        return null;
    }
    return {
        architectureId: descriptor.id,
        modelProfileId: model.modelProfileId,
        model: model.value,
        extras: [...(model.enhancements?.extras ?? descriptor.extras ?? [])],
        capabilities: structuredClone(descriptor.capabilities),
        ...(model.entryAbilities === undefined
            ? {}
            : { entryAbilities: [...model.entryAbilities] }),
        entryModes: [...model.entryModes],
    };
};

/**
 * Plans and applies one architecture conversion on a private clone.
 *
 * Preview and reducer both consume this function, so the confirmation summary
 * cannot drift from the actual atomic mutation.
 */
export const planArchitectureConversion = (
    source: Clip,
    requested: ArchitectureRetargetPlan,
    catalog: ArchitectureModelCatalog | null,
    generatedEntryMode: GeneratedEntryMode = "text-to-video",
): ArchitectureConversionPlan | null => {
    const target = resolveArchitectureRetarget(requested, catalog);
    if (!target) {
        return null;
    }

    if (
        !modelSupportsAllActiveStageEntries(target, source, generatedEntryMode)
    ) {
        return null;
    }

    const clip = structuredClone(source);
    clip.architecture = target.architectureId;
    clip.modelProfileId = target.modelProfileId;
    for (const stage of clip.stages) {
        stage.model = target.model;
        stage.modelProfileId = target.modelProfileId;
    }
    const payloadOwner =
        source.architecture === NONE_ARCHITECTURE_ID
            ? (modelIdentityFromCatalog(catalog, source.stages[0]?.model ?? "")
                  ?.architectureId ?? source.architecture)
            : source.architecture;
    const dropsForeignPayload =
        payloadOwner !== target.architectureId &&
        clip.architecturePayload !== null;
    if (dropsForeignPayload) {
        // Opaque payloads are the one exception to dormant preservation: without an owner
        // envelope a different adapter could interpret foreign schema as its own.
        clip.architecturePayload = null;
    }

    return {
        clip,
        removals: dropsForeignPayload ? ["architecture-specific payload"] : [],
        removedEntityIds: [],
        selectionAffected: false,
    };
};
