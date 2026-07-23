import type { ArchitectureModelCatalog } from "../architectures/types";
import type { BoundaryOut } from "../types";

/** The host's starting material for a VideoStages execution path. */
export type VideoHostEntryHint =
    | "text-to-video"
    | "host-image-guidance"
    | "init-image-guidance"
    | "source-video"
    | "source-video-only"
    | "global-refine-video";

export interface VideoExecutionContext {
    /** Catalog used to resolve architecture IDs into user-facing labels. */
    catalog?: ArchitectureModelCatalog;
    /**
     * Explicitly identifies the separate Refine Video action, or overrides
     * normal clip-zero inference for callers that already know the entry path.
     */
    entryPoint?: VideoHostEntryHint;
    /**
     * How an otherwise generated clip zero entered VideoStages. Source video
     * and initial-image guidance are still derived from clip zero itself.
     */
    generatedEntry?: Extract<
        VideoHostEntryHint,
        "text-to-video" | "host-image-guidance"
    >;
    /**
     * The global Refine Video path normalizes these authored clip-zero stages
     * to control-only passes, including an effective upscale of 1×.
     */
    refineSkipStages?: number;
    /** The host prompt inherited by clips without a clip-local major prompt. */
    globalPrompt?: string;
}

export type VideoTimelineShape =
    | "no-executable-clips"
    | "single-clip-no-stage"
    | "single-clip-single-stage"
    | "single-clip-multi-stage"
    | "multi-clip-no-stage"
    | "multi-clip-mixed-stages"
    | "multi-clip-single-stage-each"
    | "multi-clip-multi-stage";

export type VideoClipExecutionKind =
    | "skipped"
    | "generated"
    | "source-video"
    | "source-video-only";

export interface VideoReferenceSummary {
    clipNumber: number;
    frame: number;
    fromEnd: boolean;
    source: string;
    label: string;
}

export interface VideoBoundarySummary {
    leftClipNumber: number;
    rightClipNumber: number;
    /** @deprecated Prefer `requested`; retained for compatibility. */
    kind: BoundaryOut;
    requested: BoundaryOut;
    effective: BoundaryOut;
    fallback:
        | "none"
        | "target-is-sourced-video"
        | "target-has-no-stage"
        | "target-has-first-frame-reference"
        | "cross-architecture"
        | "architecture-rule";
    overlapFrames: number;
    label: string;
}

export interface VideoClipAudioSummary {
    clipNumber: number;
    source: string;
    label: string;
    lengthFromAudio: boolean;
    lengthFromControlNet: boolean;
    reusesStageAudio: boolean;
    savesTrack: boolean;
    segmentCount: number;
}

export interface VideoClipPathSummary {
    clipNumber: number;
    kind: VideoClipExecutionKind;
    architectureId: string;
    architectureLabel: string;
    modelProfileId: string;
    stageCount: number;
    activeStageCount: number;
    label: string;
}

export interface VideoAuthoredAudioSpanSummary {
    trackId: string;
    spanId: string;
    firstClipNumber: number | null;
    lastClipNumber: number | null;
    clipNumbers: number[];
    pending: boolean;
    label: string;
}

export interface VideoAuthoredAudioTrackSummary {
    trackId: string;
    sourceKind: string;
    sourceReference: string;
    sourceUploadFileName: string | null;
    spanCount: number;
    clipNumbers: number[];
    pendingSpanCount: number;
    spans: VideoAuthoredAudioSpanSummary[];
    label: string;
}

export interface VideoExecutionPathSummary {
    engine: "VideoStages";
    hostEntry: { kind: VideoHostEntryHint; label: string };
    shape: { kind: VideoTimelineShape; label: string };
    counts: {
        clips: number;
        executableClips: number;
        stages: number;
        activeStages: number;
        authoredAudioTracks: number;
        authoredAudioSpans: number;
    };
    clips: VideoClipPathSummary[];
    boundaries: VideoBoundarySummary[];
    features: {
        sourceVideoClipNumbers: number[];
        sourceVideoOnlyClipNumbers: number[];
        upscaledStageCount: number;
        icLoraCount: number;
        loraCount: number;
        majorPromptClipNumbers: number[];
        majorPromptOverrideClipNumbers: number[];
        majorPromptInheritedClipNumbers: number[];
        relayPromptCount: number;
        retakeClipNumbers: number[];
        references: VideoReferenceSummary[];
        audio: {
            clips: VideoClipAudioSummary[];
            segmentCount: number;
            lengthFromAudioClipNumbers: number[];
            lengthFromControlNetClipNumbers: number[];
            authoredTracks: VideoAuthoredAudioTrackSummary[];
        };
    };
    /** Compact, display-ready labels; deliberately avoids implementation nodes. */
    labels: string[];
}
