import type {
    ArchitectureCatalogEntryDto,
    ArchitectureCatalogView,
    ArchitectureModelCatalog,
    ArchitectureModelEntry,
    ArchitectureRetargetPlan,
    VideoArchitectureId,
} from "./types";

/** The catalog's descriptor for one architecture id. */
export const architectureDescriptor = (
    catalog: ArchitectureModelCatalog | null | undefined,
    architectureId: string | null | undefined,
): ArchitectureCatalogEntryDto | null =>
    (architectureId
        ? catalog?.architectures.find((entry) => entry.id === architectureId)
        : null) ?? null;

/** The catalog entry for one model value. */
export const modelCatalogEntry = (
    catalog: ArchitectureModelCatalog | null | undefined,
    model: string | null | undefined,
): ArchitectureModelEntry | null =>
    (model ? catalog?.entries.find((entry) => entry.value === model) : null) ??
    null;

export const architectureCatalogView = (
    catalog: ArchitectureModelCatalog,
    architectureId: VideoArchitectureId,
): ArchitectureCatalogView => {
    const catalogArchitecture = architectureDescriptor(catalog, architectureId);
    const entries = catalog.entries.filter(
        (entry) => entry.architectureId === architectureId,
    );
    return {
        architectureId,
        architectureLabel: catalogArchitecture?.label ?? architectureId,
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
    modelCatalogEntry(catalog, model)?.architectureId ?? null;

export const modelProfileForModel = (
    catalog: ArchitectureModelCatalog,
    model: string,
): string | null => modelCatalogEntry(catalog, model)?.modelProfileId ?? null;

export const buildArchitectureRetargetPlan = (
    catalog: ArchitectureModelCatalog,
    model: string,
): ArchitectureRetargetPlan | null => {
    const entry = modelCatalogEntry(catalog, model);
    const architectureId = entry?.architectureId ?? null;
    const descriptor = architectureDescriptor(catalog, architectureId);
    const profileId = entry?.modelProfileId ?? null;
    const capabilities = entry?.capabilities ?? descriptor?.capabilities;
    return entry && architectureId && profileId && descriptor && capabilities
        ? {
              architectureId,
              modelProfileId: profileId,
              model,
              capabilities: structuredClone(capabilities),
              entryModes: [...entry.entryModes],
          }
        : null;
};
