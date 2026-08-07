import type { Clip } from "../../types";
import { architectureDescriptor, modelCatalogEntry } from "../catalogQueries";
import type {
    ArchitectureModelCatalog,
    ArchitectureRetargetPlan,
} from "../types";

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
    };
};

/** Retargets every stage onto the resolved model, on a private clone. */
export const planArchitectureConversion = (
    source: Clip,
    requested: ArchitectureRetargetPlan,
    catalog: ArchitectureModelCatalog | null,
): Clip | null => {
    const target = resolveArchitectureRetarget(requested, catalog);
    if (!target) {
        return null;
    }

    const clip = structuredClone(source);
    clip.architectureHint = target.architectureId;
    clip.modelProfileId = target.modelProfileId;
    for (const stage of clip.stages) {
        stage.model = target.model;
        stage.modelProfileId = target.modelProfileId;
    }
    return clip;
};
