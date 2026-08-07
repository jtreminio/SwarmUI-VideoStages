import type { FrameGridSpec } from "../../renderUtils";
import type { BoundaryOut, Clip, Stage } from "../../types";
import type { BoundaryWindowConstraints } from "../boundaryConstraints";
import type { GeneratedArchitectureFeature } from "../generatedFeatures";
import type { FrameGridResolution } from "../temporalGrid";
import type { ArchitectureModelCatalog } from "../types";

export interface CapabilityDecision {
    supported: boolean;
    reason: string;
    /** Diagnostic code when a named precondition blocks the feature; empty otherwise. */
    code: string;
}

export interface AuthoringState extends CapabilityDecision {
    visible: boolean;
    enabled: boolean;
}

export interface ClipCapabilityView {
    architectureId: string;
    architectureLabel: string;
    known: boolean;
    /** Compatible temporal grid of every resolved active-stage model. */
    frameGrid: FrameGridSpec;
    /** Distinguishes a neutral grid from missing facts or an unrepresentable combination. */
    frameGridResolution: FrameGridResolution;
    hasGenerationStage: boolean;
    audioSourceKinds: readonly string[];
    /** Audio sourcing is stated by `audioSourceKinds`, not by a feature flag. */
    clipAudio: CapabilityDecision;
    decision(feature: GeneratedArchitectureFeature): CapabilityDecision;
    authoringState(
        feature: GeneratedArchitectureFeature,
        persisted: boolean,
    ): AuthoringState;
}

export interface StageCapabilityView {
    decision(
        feature: "stageLoras" | "sampler" | "scheduler",
    ): CapabilityDecision;
    authoringState(
        feature: "stageLoras" | "sampler" | "scheduler",
        persisted: boolean,
    ): AuthoringState;
}

export interface BoundaryCapabilityView {
    leftClipIdx: number;
    rightClipIdx: number | null;
    modes: readonly BoundaryOut[];
    crossArchitecture: boolean;
    reason: string;
    windowConstraints(mode: BoundaryOut): BoundaryWindowConstraints;
    effective(requested: BoundaryOut): BoundaryOut;
}

export interface CapabilityViewResolver {
    catalog: ArchitectureModelCatalog;
    forClip(clip: Clip): ClipCapabilityView;
    forStage(clip: Clip, stage: Stage): StageCapabilityView;
    forBoundary(
        left: Clip,
        right: Clip | null,
        leftClipIdx?: number,
        rightClipIdx?: number | null,
    ): BoundaryCapabilityView;
    forBoundaryIndex(
        clips: readonly Clip[],
        leftClipIdx: number,
    ): BoundaryCapabilityView;
    executableClipIndexes(clips: readonly Clip[]): number[];
}
