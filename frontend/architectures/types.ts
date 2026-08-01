export type VideoArchitectureId = string;
export type ModelProfileId = string;

import type { GeneratedAuthoringFeature } from "./generatedFeatures";

export type CatalogAuthoringFeature = GeneratedAuthoringFeature;

export type CapabilitySupport = "supported" | "unsupported" | "conditional";
export type CapabilityRuleScope = "clip" | "stage" | "boundary";

export interface CapabilityRuleDecision {
    support: CapabilitySupport;
    code: string;
    reason: string;
    scope: CapabilityRuleScope;
    constraints: Record<string, unknown> | null;
}

export interface ArchitectureCapabilities {
    clip: string[];
    stage: string[];
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
    /** Internal entry-mode projection derived from typed model capabilities. */
    entryModes: string[];
}

export interface ModelEnhancements {
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
    entryAbilities: string[];
    enhancements: ModelEnhancements;
}

export interface ArchitectureCatalogEntryDto {
    id: VideoArchitectureId;
    label: string;
    capabilities: ArchitectureCapabilities;
    boundaryRules: Record<string, CapabilityRuleDecision>;
    rules: CapabilityRuleDecision[];
}

/** Serializable projection supplied by the authoritative backend catalog. */
export interface VideoArchitectureCatalogDto {
    schemaVersion: 2;
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
