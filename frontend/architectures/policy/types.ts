import type { BoundaryOut, Clip, Stage } from "../../types";
import type { BoundaryOverlapConstraints } from "../boundaryConstraints";
import type { FrameGridResolution } from "../temporalGrid";
import type {
    ArchitectureModelCatalog,
    CapabilityRuleDecision,
    CatalogAuthoringFeature,
} from "../types";

export type AuthoringFeature = CatalogAuthoringFeature;

export interface CapabilityDecision {
    supported: boolean;
    reason: string;
    rule: CapabilityRuleDecision | null;
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
    frameGrid: number;
    /** Distinguishes a neutral grid from missing facts or an unrepresentable combination. */
    frameGridResolution: FrameGridResolution;
    audioSourceKinds: readonly string[];
    decision(feature: AuthoringFeature): CapabilityDecision;
    authoringState(
        feature: AuthoringFeature,
        persisted: boolean,
    ): AuthoringState;
}

export interface StageCapabilityView {
    upscaleModes: readonly string[];
    decision(
        feature: "stageLoras" | "upscale" | "sampler" | "scheduler",
    ): CapabilityDecision;
    authoringState(
        feature: "stageLoras" | "upscale" | "sampler" | "scheduler",
        persisted: boolean,
    ): AuthoringState;
}

export interface BoundaryCapabilityView {
    leftClipIdx: number;
    rightClipIdx: number | null;
    modes: readonly BoundaryOut[];
    crossArchitecture: boolean;
    reason: string;
    overlapConstraints(mode: BoundaryOut): BoundaryOverlapConstraints;
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
