import type { BoundaryOut, Clip, Stage } from "../../types";
import type { BoundaryOverlapConstraints } from "../boundaryConstraints";
import type {
    ArchitectureModelCatalog,
    CapabilityRuleDecision,
} from "../types";

export type AuthoringFeature =
    | "multiStage"
    | "sourceVideo"
    | "frameReferences"
    | "referenceFraming"
    | "retake"
    | "majorPrompt"
    | "promptRelay"
    | "clipAudio"
    | "audioReuse"
    | "stageLoras"
    | "icLora"
    | "hdr"
    | "upscale";

/**
 * Context a conditional capability rule needs beyond the clip itself. Omitted
 * fields simply leave the dependent rules inert.
 */
export interface CapabilityRuleScopeContext {
    /** True only while authoring an explicit global Refine Video invocation. */
    globalRefineMode?: boolean;
    /** Executable timeline clips, for timeline-uniformity rules. */
    timelineClips?: readonly Clip[];
}

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
