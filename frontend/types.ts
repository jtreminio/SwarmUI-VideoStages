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
    loraDefaultWeights: (number | null)[];
    samplerValues: string[];
    samplerLabels: string[];
    schedulerValues: string[];
    schedulerLabels: string[];
    upscaleMethodValues: string[];
    upscaleMethodLabels: string[];
    width: number;
    height: number;
    aspectRatio?: string;
    sideLength?: number | null;
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
    fps: number;
    dimsExplicit: boolean;
    clips: Clip[];
    audioTracks?: AudioTrack[];
}

export const CURRENT_AUTHORING_SCHEMA_VERSION = 7;

export interface UploadedMedia {
    data: string;
    fileName: string | null;
}

export interface ClipLora {
    name: string;
}

/**
 * Opaque JSON owned by the clip's architecture. Common authoring, normalization,
 * and persistence code carry this envelope but must not interpret or project its
 * nested fields. Nothing parses it today: it exists so a document written by a
 * future architecture survives a round trip through this one.
 */
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
    /** Per-stage weight for each clip LoRA, aligned by clip LoRA index. */
    loraWeights: number[];
    frameRefStrengths: number[];
    upscale: number;
    upscaleMethod: string;
    model: string;
    modelProfileId: ModelProfileId;
    steps: number;
    cfgScale: number;
    sampler: string;
    scheduler: string;
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
export interface InitVideo {
    data: string;
    fileName: string | null;
    fps: number;
    durationSeconds: number;
    startSeconds: number;
    lengthSeconds: number;
}

export type ClipReferenceKind = "image" | "video" | "audio";

/**
 * One whole-clip reference: media the model conditions on with no frame
 * position. The architecture presents references to its text encoder as
 * numbered items the prompt names by tag, so the authored order is meaningful.
 * `source` is "Upload" for `uploadedMedia`, a ControlNet input for any media
 * kind, a host capture ("Base", "Refiner") for images, or an AceStepFun track
 * for audio. `includeSoundtrack` asks a video reference to also pass its own
 * audio track as the paired reference audio.
 * `mediaDurationSeconds` is the browser-probed length of the picked media
 * (0 = unknown); when `drivesClipLength` is set the clip's own duration follows
 * it, so at most one reference per clip may claim it. `mediaScale` downsamples
 * a video reference before it is presented, trading detail for speed.
 */
export interface ClipReference {
    id?: string;
    kind: ClipReferenceKind;
    source: string;
    uploadedMedia: UploadedMedia | null;
    includeSoundtrack: boolean;
    mediaDurationSeconds: number;
    drivesClipLength: boolean;
    mediaScale: number;
}

export interface FrameRefImage {
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
export type ReferenceFraming = "crop" | "stretch" | "fit" | "fit-green";

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
    id?: string;
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
    /** Cached authoring label/repair hint. Resolved stage-0 model identity owns behavior. */
    architectureHint: VideoArchitectureId;
    modelProfileId: ModelProfileId;
    skipped: boolean;
    hue: number;
    boundaryOut: BoundaryOut;
    /**
     * For a continue/crossfade overlap, preserve this clip's outgoing audio
     * tail as opening generation context for the next clip.
     */
    boundaryOutCarryAudio: boolean;
    /** Scale applied to an automatic reference-mode Continue video. */
    boundaryOutReferenceScale: number;
    /** Whether that automatic reference video also supplies its soundtrack. */
    boundaryOutReferenceIncludeSoundtrack: boolean;
    /** Architecture-normalized frame window used by the selected outgoing join. */
    boundaryOutOverlap: number;
    duration: number;
    /** How reference media is fitted to this clip's generation dimensions. */
    refFraming: ReferenceFraming;
    audioSource: string;
    /** Normal LoRA model definitions shared by every stage in this clip. */
    loras: ClipLora[];
    icLoras: IcLora[];
    saveAudioTrack: boolean;
    clipLengthFromAudio: boolean;
    clipLengthFromControlNet: boolean;
    reuseAudio: boolean;
    uploadedAudio: UploadedMedia | null;
    prompt: string;
    promptWindows: PromptWindow[];
    retake: Retake | null;
    initVideo: InitVideo | null;
    references: ClipReference[];
    frameRefs: FrameRefImage[];
    stages: Stage[];
}

export type AudioTrackSourceKind =
    | "Upload"
    | "AceStepFun"
    | "Native"
    | "ControlNet"
    | "External";

/** Source identity for a timeline-wide audio span. */
export interface AudioTrackSource {
    kind: AudioTrackSourceKind;
    reference: string;
    uploadedAudio: UploadedMedia | null;
}

/**
 * One addressable interval of a root audio track, expressed as a timeline
 * seconds window plus the matching offset into the source audio.
 */
export interface AudioTrackSpan {
    id?: string;
    timelineStartSeconds: number | null;
    timelineLengthSeconds: number | null;
    sourceStartSeconds: number;
}

/**
 * A logical timeline-wide audio span. New authoring creates exactly one
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

export type CanonicalStage = WithRequiredId<Stage>;
export type CanonicalIcLora = WithRequiredId<IcLora>;
export type CanonicalPromptWindow = WithRequiredId<PromptWindow>;
export type CanonicalRetake = WithRequiredId<Retake>;
export type CanonicalFrameRefImage = WithRequiredId<FrameRefImage>;
export type CanonicalClipReference = WithRequiredId<ClipReference>;
export type CanonicalAudioTrackSpan = WithRequiredId<AudioTrackSpan>;
export type CanonicalAudioTrack = Omit<WithRequiredId<AudioTrack>, "spans"> & {
    spans: CanonicalAudioTrackSpan[];
};
export type CanonicalClip = Omit<
    WithRequiredId<Clip>,
    | "icLoras"
    | "promptWindows"
    | "retake"
    | "references"
    | "frameRefs"
    | "stages"
> & {
    icLoras: CanonicalIcLora[];
    promptWindows: CanonicalPromptWindow[];
    retake: CanonicalRetake | null;
    references: CanonicalClipReference[];
    frameRefs: CanonicalFrameRefImage[];
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
    StoredClipReference,
    StoredFrameRefImage,
    StoredStage,
} from "./storageTypes";
export {
    STORED_CLIP_KEYS,
    STORED_CLIP_REFERENCE_KEYS,
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
