import type {
    FRAME_REFERENCE_POSITIONS,
    RULE_SUPPORTS,
} from "./generatedFeatures";

export type VideoArchitectureId = string;
export type ModelProfileId = string;

export type CapabilitySupport = (typeof RULE_SUPPORTS)[number];
export type FrameReferencePosition = (typeof FRAME_REFERENCE_POSITIONS)[number];

export interface CapabilityRuleDecision {
    support: CapabilitySupport;
    code: string;
    reason: string;
    constraints: Record<string, unknown> | null;
}

export interface ArchitectureCapabilities {
    features: string[];
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
    frameGridOrigin?: number | null;
    /** Complete effective support for this resolved model; absent only for host-only unknowns. */
    capabilities?: ArchitectureCapabilities;
    enhancements?: ModelEnhancements;
    /** Internal entry-mode projection derived from typed model capabilities. */
    entryModes: string[];
}

export interface ModelEnhancements {
    referencePositions: FrameReferencePosition[];
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
}

export interface ArchitectureCatalogModelDto {
    modelName: string;
    architectureId: VideoArchitectureId;
    modelProfileId: ModelProfileId;
    modelClassId: string;
    compatibilityClassId: string;
    frameGrid: number;
    frameGridOrigin: number;
    capabilities: ArchitectureCapabilities;
    enhancements: ModelEnhancements;
}

export interface ArchitectureCatalogEntryDto {
    id: VideoArchitectureId;
    label: string;
    capabilities: ArchitectureCapabilities;
    boundaryRules: Record<string, CapabilityRuleDecision>;
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
