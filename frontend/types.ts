import type {
    ArchitectureModelCatalog,
    ModelProfileId,
    VideoArchitectureId,
} from "./architectures/types";

export interface RootDefaults {
    modelValues: string[];
    modelLabels: string[];
    modelCatalog: ArchitectureModelCatalog;
    loraValues: string[];
    loraLabels: string[];
    samplerValues: string[];
    samplerLabels: string[];
    schedulerValues: string[];
    schedulerLabels: string[];
    upscaleMethodValues: string[];
    upscaleMethodLabels: string[];
    width: number;
    height: number;
    fps: number;
    frames: number;
    control: number;
    controlMin: number;
    controlMax: number;
    controlStep: number;
    upscale: number;
    upscaleMin: number;
    upscaleMax: number;
    upscaleStep: number;
    steps: number;
    stepsMin: number;
    stepsMax: number;
    stepsStep: number;
    cfgScale: number;
    cfgScaleMin: number;
    cfgScaleMax: number;
    cfgScaleStep: number;
}

export interface VideoStagesConfig {
    /**
     * Version of the frontend authoring document. Only the current schema is
     * accepted; older and unversioned carriers are intentionally rejected.
     */
    schemaVersion?: number;
    width: number;
    height: number;
    /**
     * Always mirrors the core Video FPS param: the panel's FPS field writes
     * through to the core input and re-reads it as the inherited value, so
     * fps is never serialized into the authoring document.
     */
    fps: number;
    dimsExplicit: boolean;
    clips: Clip[];
    /**
     * Timeline-wide overlay audio. Each logical track is one independently
     * movable audio segment lane; its timeline window may cross clip seams.
     */
    audioTracks?: AudioTrack[];
}

export const CURRENT_AUTHORING_SCHEMA_VERSION = 4;

export interface UploadedMedia {
    data: string;
    fileName: string | null;
}

export interface StageLora {
    name: string;
    weight: number;
}

/**
 * One overlay audio piece placed on a clip's audio lane, in addition to the
 * clip's base audio source. `source` is either an uploaded audio blob (data
 * URI + fileName) or an AceStepFun track ref string ("audio0", "audio1", …).
 * `startSeconds` is where the piece begins inside the clip;
 * `trimStartSeconds` is how far into the source file playback starts; and
 * `lengthSeconds` is how long the piece plays. `volume` is its relative
 * loudness before the additive mix: 1 is unchanged, lower is quieter, and
 * higher is louder. Timeline values use a 0.1-second step.
 */
export interface AudioSegment {
    id?: string;
    source: UploadedMedia | string | null;
    startSeconds: number;
    trimStartSeconds: number;
    lengthSeconds: number;
    volume: number;
}

export interface Stage {
    id?: string;
    skipped: boolean;
    control: number;
    /**
     * Legacy stage-wide IC-LoRA strength retained as the fallback for documents
     * authored before per-guide strengths were introduced.
     */
    controlNetStrength: number;
    /** Per-stage strength for each clip IC-LoRA, aligned by IC-LoRA index. */
    icLoraStrengths: number[];
    refStrengths: number[];
    upscale: number;
    upscaleMethod: string;
    model: string;
    modelProfileId: ModelProfileId;
    steps: number;
    cfgScale: number;
    sampler: string;
    scheduler: string;
    loras: StageLora[];
}

export interface PromptWindow {
    /**
     * Prompt windows ride the prompt carrier, whose syntax has no ID slot.
     * Persistence keeps this ID in the versioned UI-state sidecar when
     * possible; it is regenerated if that browser-local sidecar is absent.
     */
    id?: string;
    prompt: string;
    start: number;
    duration: number;
}

/**
 * Optional per-clip retake window (seconds). Regenerates only frames inside
 * [startSeconds, startSeconds + lengthSeconds) of a refined base video while the
 * rest stay locked; `strength` is the per-frame noise-mask value inside it.
 */
export interface Retake {
    id?: string;
    startSeconds: number;
    lengthSeconds: number;
    strength: number;
}

/**
 * Pre-existing footage used as the clip's starting point instead of a
 * from-scratch generation. `data`/`fileName` are the picked video (data URI +
 * name — both browser uploads and server-picked files resolve to a data URI);
 * `fps` and `durationSeconds` are the file's probed metadata (display only,
 * 0 = unknown); `startSeconds`/`lengthSeconds` select the used range inside
 * the file, and the clip's own duration follows `lengthSeconds`. The backend
 * conforms the range to the timeline (fps resample, exact frame window,
 * resize) and feeds it to the clip's stage chain as per-clip refine input:
 * stage 0 refines it according to its Control value, later stages
 * refine/upscale it, and a retake window regenerates part of it.
 */
export interface SourceVideo {
    data: string;
    fileName: string | null;
    fps: number;
    durationSeconds: number;
    startSeconds: number;
    lengthSeconds: number;
}

export interface RefImage {
    id?: string;
    source: string;
    uploadFileName: string | null;
    uploadedImage: UploadedMedia | null;
    frame: number;
    fromEnd: boolean;
}

export type BoundaryOut = "cut" | "continue" | "crossfade";

export type IcLoraControlType = "none" | "canny" | "depth" | "normal";
export type IcLoraDriveData = "none" | "visual" | "audio";
export type IcLoraDriveMediaKind = "image" | "video" | "audio";

/**
 * One in-context LoRA on a clip. `lora` is the LoRA model name; `preset` is a
 * curated LTX catalog id ("custom" = normal visual-drive behavior) and may
 * select an architecture-owned Drive Media stream contract. `strength` is the
 * LoRA model strength; an
 * `attentionStrength` below 1 switches the backend to the Advanced guide node
 * (per-guide self-attention influence); `controlType` renders the drive video
 * into a control signal before guiding; `driveMedia` is the uploaded drive
 * media (data URI + name). `driveData` explicitly selects which stream is
 * extracted: visual frames, audio, or none for a model-only patch.
 * `driveMediaKinds` declares the media containers that may supply that stream.
 * Curated presets seed both values; Custom exposes the stream choice directly.
 * `driveSource` is
 * "Upload" for per-entry media or "Incoming" for media already entering the
 * target generation point (init/source media, prior-stage output, or available
 * previous-clip context). `stage` restricts the entry to one stage index
 * (-1 = every stage).
 */
export interface IcLora {
    lora: string;
    preset: string;
    driveSource: string;
    driveData: IcLoraDriveData;
    driveMediaKinds: IcLoraDriveMediaKind[];
    stage: number;
    strength: number;
    attentionStrength: number;
    controlType: IcLoraControlType;
    driveMedia: UploadedMedia | null;
}

export interface Clip {
    id?: string;
    architecture: VideoArchitectureId;
    modelProfileId: ModelProfileId;
    skipped: boolean;
    hue: number;
    boundaryOut: BoundaryOut;
    /**
     * For a continue/crossfade overlap, preserve this clip's outgoing audio
     * tail as opening generation context for the next clip.
     */
    boundaryOutCarryAudio: boolean;
    /**
     * "continue" boundary overlap in frames (multiple of 8): the next clip is
     * generated with this clip's last overlap+1 frames as frozen latent
     * context, and the merge collapses the duplicated frames. Ignored for
     * "cut"/"crossfade".
     */
    boundaryOutOverlap: number;
    duration: number;
    audioSource: string;
    icLoras: IcLora[];
    saveAudioTrack: boolean;
    clipLengthFromAudio: boolean;
    clipLengthFromControlNet: boolean;
    reuseAudio: boolean;
    uploadedAudio: UploadedMedia | null;
    audioSegments: AudioSegment[];
    prompt: string;
    promptWindows: PromptWindow[];
    retake: Retake | null;
    sourceVideo: SourceVideo | null;
    refs: RefImage[];
    stages: Stage[];
}

export type AudioTrackSourceKind =
    | "Upload"
    | "AceStepFun"
    | "Native"
    | "ControlNet"
    | "External";

/** Source identity for a timeline-wide audio segment. */
export interface AudioTrackSource {
    kind: AudioTrackSourceKind;
    reference: string;
    uploadedAudio: UploadedMedia | null;
}

/**
 * One addressable interval of a root audio track. Clip endpoints are inclusive
 * stable clip IDs; a missing first or last endpoint means timeline start or
 * timeline end. With both endpoints missing, a complete timeline start/length
 * owns the span. A timeline window and clip range may coexist (intersection);
 * a clip-relative window requires the same concrete clip at both endpoints.
 */
export interface AudioTrackSpan {
    id?: string;
    firstClipId: string | null;
    lastClipId: string | null;
    timelineStartSeconds: number | null;
    timelineLengthSeconds: number | null;
    sourceStartSeconds: number;
    clipStartOffsetSeconds: number | null;
    clipLengthSeconds: number | null;
}

/**
 * A logical timeline-wide audio segment. New authoring creates exactly one
 * span per track; the array remains for compatibility with the earlier
 * planned-track schema and is normalized into independent lanes on load.
 */
export interface AudioTrack {
    id?: string;
    source: AudioTrackSource;
    spans: AudioTrackSpan[];
    /** Relative loudness before additive mixing. */
    volume?: number;
}

type WithRequiredId<T extends { id?: string }> = Omit<T, "id"> & {
    id: string;
};

export type CanonicalAudioSegment = WithRequiredId<AudioSegment>;
export type CanonicalStage = WithRequiredId<Stage>;
export type CanonicalPromptWindow = WithRequiredId<PromptWindow>;
export type CanonicalRetake = WithRequiredId<Retake>;
export type CanonicalRefImage = WithRequiredId<RefImage>;
export type CanonicalAudioTrackSpan = WithRequiredId<AudioTrackSpan>;
export type CanonicalAudioTrack = Omit<WithRequiredId<AudioTrack>, "spans"> & {
    spans: CanonicalAudioTrackSpan[];
};
export type CanonicalClip = Omit<
    WithRequiredId<Clip>,
    "audioSegments" | "promptWindows" | "retake" | "refs" | "stages"
> & {
    audioSegments: CanonicalAudioSegment[];
    promptWindows: CanonicalPromptWindow[];
    retake: CanonicalRetake | null;
    refs: CanonicalRefImage[];
    stages: CanonicalStage[];
};
export type CanonicalVideoStagesConfig = Omit<
    VideoStagesConfig,
    "schemaVersion" | "clips" | "audioTracks"
> & {
    schemaVersion: number;
    clips: CanonicalClip[];
    audioTracks: CanonicalAudioTrack[];
};

export type {
    StoredClip,
    StoredRefImage,
    StoredStage,
} from "./storageTypes";
export {
    STORED_CLIP_KEYS,
    STORED_REF_KEYS,
    STORED_STAGE_KEYS,
    UNSTORED_CLIP_KEYS,
} from "./storageTypes";

export const REF_SOURCE_BASE = "Base";
export const REF_SOURCE_REFINER = "Refiner";
export const REF_SOURCE_UPLOAD = "Upload";

export interface ImageSourceOption {
    value: string;
    label: string;
    disabled?: boolean;
}

export type { TimelineSelection } from "./selectionTypes";
