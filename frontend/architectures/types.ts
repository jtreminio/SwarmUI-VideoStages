export type VideoArchitectureId = string;
export type ModelProfileId = string;

import type { GeneratedAuthoringFeature } from "./generatedFeatures";

export { AUTHORING_FEATURES } from "./generatedFeatures";
export type CatalogAuthoringFeature = GeneratedAuthoringFeature;

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

export interface MinimumStageControlRuleConstraints {
    exclusiveMinimumControl: number;
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
    modelClassId: string | null;
    compatibilityClassId: string | null;
    frameGrid?: number | null;
    /** Complete effective support for this resolved model; absent only for host-only unknowns. */
    capabilities?: ArchitectureCapabilities;
    entryAbilities?: string[];
    enhancements?: ModelEnhancements;
    /** Legacy alias retained while persisted profile identities migrate. */
    entryModes: string[];
}

export interface ModelEnhancements {
    extras: string[];
    referencePositions: string[];
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
    extras?: string[];
    entryAbilities?: string[];
    entryModes: string[];
}

export interface ArchitectureCatalogModelDto {
    modelName: string;
    architectureId: VideoArchitectureId;
    modelProfileId: ModelProfileId;
    modelClassId: string;
    compatibilityClassId: string;
    frameGrid: number;
    capabilities: ArchitectureCapabilities;
    entryAbilities?: string[];
    enhancements?: ModelEnhancements;
    /** Legacy alias retained for older clients. */
    entryModes: string[];
}

export interface ArchitectureCatalogEntryDto {
    id: VideoArchitectureId;
    label: string;
    defaultProfileId: ModelProfileId;
    extras?: string[];
    /** Scoped legacy transport; new feature reads use `extras`. */
    capabilities: ArchitectureCapabilities;
    profiles: VideoModelProfileDescriptor[];
    boundaryRules: Record<string, CapabilityRuleDecision>;
    rules: CapabilityRuleDecision[];
}

export interface VideoModelProfileDescriptor {
    id: ModelProfileId;
    label: string;
    /** Legacy aliases retained for older catalogs and saved profile hints. */
    entryModes: string[];
    /** Legacy transport only; production feature policy ignores this list. */
    capabilities: string[];
    /** Legacy transport only; architecture rules own executable policy. */
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
