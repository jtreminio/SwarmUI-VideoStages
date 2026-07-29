export type VideoArchitectureId = string;
export type ModelProfileId = string;

export type CapabilitySupport = "supported" | "unsupported" | "conditional";
export type CapabilityRuleScope =
    | "architecture"
    | "model-profile"
    | "clip"
    | "stage"
    | "boundary"
    | "output";

export interface CapabilityRuleDecision {
    support: CapabilitySupport;
    code: string;
    reason: string;
    scope: CapabilityRuleScope;
    entityId: string | null;
    constraints: Record<string, unknown> | null;
}

export interface ArchitectureCapabilities {
    architecture: string[];
    clip: string[];
    stage: string[];
    output: string[];
    upscaleModes: string[];
    entryModes: string[];
    audioSourceKinds: string[];
}

export interface ArchitectureModelEntry {
    value: string;
    label: string;
    architectureId: VideoArchitectureId | null;
    modelProfileId: ModelProfileId | null;
}

export interface ArchitectureModelCatalog {
    entries: ArchitectureModelEntry[];
    architectures: ArchitectureCatalogEntryDto[];
    source: "backend" | "unavailable";
}

export interface ArchitectureCatalogView {
    architectureId: VideoArchitectureId;
    architectureLabel: string;
    values: string[];
    labels: string[];
}

export interface ArchitectureRetargetPlan {
    architectureId: VideoArchitectureId;
    modelProfileId: ModelProfileId;
    model: string;
    capabilities: ArchitectureCapabilities;
}

export interface ArchitectureCatalogModelDto {
    modelName: string;
    architectureId: VideoArchitectureId;
    modelProfileId: ModelProfileId;
    compatId?: string | null;
}

export interface ArchitectureCatalogEntryDto {
    id: VideoArchitectureId;
    label: string;
    defaultProfileId: ModelProfileId;
    capabilities: ArchitectureCapabilities;
    profiles: VideoModelProfileDescriptor[];
    boundaryRules: Record<string, CapabilityRuleDecision>;
    rules: CapabilityRuleDecision[];
}

export interface VideoModelProfileDescriptor {
    id: ModelProfileId;
    label: string;
    capabilities: string[];
    rules: CapabilityRuleDecision[];
}

/** Serializable projection supplied by the authoritative backend catalog. */
export interface VideoArchitectureCatalogDto {
    architectures: ArchitectureCatalogEntryDto[];
    models: ArchitectureCatalogModelDto[];
}

export type ArchitectureCatalogStatus =
    | "loading"
    | "unavailable"
    | "ready"
    | "refreshing"
    | "stale";

export interface ArchitectureCatalogSnapshot {
    status: ArchitectureCatalogStatus;
    catalog: VideoArchitectureCatalogDto | null;
    error: string | null;
}
