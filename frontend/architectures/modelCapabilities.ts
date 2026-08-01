import { activeStageCount } from "../clipSemantics";
import type { Clip } from "../types";
import type {
    ArchitectureCapabilities,
    ArchitectureCatalogEntryDto,
    ArchitectureModelEntry,
} from "./types";

const intersect = (
    left: readonly string[],
    right: readonly string[],
): string[] => left.filter((value) => right.includes(value));

export const effectiveModelCapabilities = (
    model: ArchitectureModelEntry | undefined,
    architecture: ArchitectureCatalogEntryDto,
): ArchitectureCapabilities => model?.capabilities ?? architecture.capabilities;

/**
 * Clip-wide authoring support is the intersection of every active resolved stage model. Unknown
 * or foreign stages fail closed instead of inheriting the stage-0 architecture's broad contract.
 */
export const effectiveClipCapabilities = (
    clip: Pick<Clip, "stages">,
    architecture: ArchitectureCatalogEntryDto,
    modelForName: (model: string) => ArchitectureModelEntry | undefined,
): ArchitectureCapabilities | null => {
    const stages = clip.stages.slice(0, activeStageCount(clip));
    if (stages.length === 0) {
        return structuredClone(architecture.capabilities);
    }
    const models = stages.map((stage) => modelForName(stage.model));
    if (
        models.some(
            (model) =>
                !model?.architectureId ||
                model.architectureId !== architecture.id,
        )
    ) {
        return null;
    }
    return (models as ArchitectureModelEntry[]).reduce((effective, model) => {
        const capabilities = effectiveModelCapabilities(model, architecture);
        return {
            clip: intersect(effective.clip, capabilities.clip),
            stage: intersect(effective.stage, capabilities.stage),
            upscaleModes: intersect(
                effective.upscaleModes,
                capabilities.upscaleModes,
            ),
            entryModes: intersect(
                effective.entryModes,
                capabilities.entryModes,
            ),
            audioSourceKinds: intersect(
                effective.audioSourceKinds,
                capabilities.audioSourceKinds,
            ),
        };
    }, structuredClone(architecture.capabilities));
};
