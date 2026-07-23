import { videoArchitectureRegistry } from "./registry";
import type {
    ArchitectureCatalogView,
    ArchitectureModelCatalog,
    ArchitectureRegistry,
    ArchitectureRetargetPlan,
    VideoArchitectureId,
} from "./types";

export const architectureCatalogView = (
    catalog: ArchitectureModelCatalog,
    architectureId: VideoArchitectureId,
    registry: ArchitectureRegistry = videoArchitectureRegistry,
): ArchitectureCatalogView => {
    const definition = registry.get(architectureId);
    const catalogArchitecture = catalog.architectures.find(
        (entry) => entry.id === architectureId,
    );
    const entries = catalog.entries.filter(
        (entry) => entry.architectureId === architectureId,
    );
    return {
        architectureId,
        architectureLabel:
            catalogArchitecture?.label ?? definition?.label ?? architectureId,
        values: entries.map((entry) => entry.value),
        labels: entries.map((entry) => entry.label),
    };
};

export const supportedArchitectureCatalog = (
    catalog: ArchitectureModelCatalog,
): ArchitectureModelCatalog => ({
    architectures: structuredClone(catalog.architectures),
    source: catalog.source,
    entries: catalog.entries.filter((entry) => entry.architectureId !== null),
});

export const architectureForModel = (
    catalog: ArchitectureModelCatalog,
    model: string,
): VideoArchitectureId | null =>
    catalog.entries.find((entry) => entry.value === model)?.architectureId ??
    null;

export const modelProfileForModel = (
    catalog: ArchitectureModelCatalog,
    model: string,
): string | null =>
    catalog.entries.find((entry) => entry.value === model)?.modelProfileId ??
    null;

export const buildArchitectureRetargetPlan = (
    catalog: ArchitectureModelCatalog,
    model: string,
    registry: ArchitectureRegistry = videoArchitectureRegistry,
): ArchitectureRetargetPlan | null => {
    const entry = catalog.entries.find(
        (candidate) => candidate.value === model,
    );
    const architectureId = entry?.architectureId ?? null;
    const descriptor = architectureId
        ? catalog.architectures.find(
              (candidate) => candidate.id === architectureId,
          )
        : null;
    const fallback = architectureId ? registry.get(architectureId) : null;
    const profileId = entry?.modelProfileId ?? null;
    const capabilities = descriptor?.capabilities ?? fallback?.capabilities;
    return architectureId && profileId && capabilities
        ? {
              architectureId,
              modelProfileId: profileId,
              model,
              capabilities: structuredClone(capabilities),
          }
        : null;
};
