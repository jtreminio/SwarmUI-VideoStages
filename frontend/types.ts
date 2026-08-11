import type {
    BOUNDARY_MODES,
    IC_LORA_CONTROL_TYPES,
    REFERENCE_FRAMINGS,
} from "./architectures/generatedFeatures";
import type {
    ArchitectureModelCatalog,
    ModelProfileId,
    VideoArchitectureId,
} from "./architectures/types";
import type { AudioTrackSourceKind } from "./generatedMediaSource";

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

export interface AuthoringDocument {
    /** Only the current schema is accepted. */
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

export interface Stage {
    id?: string;
    skipped: boolean;
    control: number;
    /** Legacy fallback for documents without per-guide strengths. */
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
    /** Regenerated when the browser-local identity sidecar is absent. */
    id?: string;
    prompt: string;
    start: number;
    duration: number;
}

export interface Retake {
    id?: string;
    startSeconds: number;
    lengthSeconds: number;
    strength: number;
}

/** Source Video is stored under the `initVideo` wire key. */
export interface InitVideo {
    data: string;
    fileName: string | null;
    fps: number;
    durationSeconds: number;
    startSeconds: number;
    lengthSeconds: number;
}

export type ClipReferenceKind = "image" | "video" | "audio";

/** Whole-clip reference. Authored order determines prompt tag numbering. */
export interface ClipReference {
    id?: string;
    kind: ClipReferenceKind;
    source: string;
    uploadedMedia: UploadedMedia | null;
    includeSoundtrack: boolean;
    mediaDurationSeconds: number;
    drivesClipLength: boolean;
    mediaScale: number;
    startSeconds: number;
    /** 0 uses the whole source. */
    lengthSeconds: number;
}

export interface FrameRefImage {
    id?: string;
    source: string;
    uploadFileName: string | null;
    uploadedImage: UploadedMedia | null;
    frame: number;
    fromEnd: boolean;
}

export type BoundaryOut = (typeof BOUNDARY_MODES)[number];

export type IcLoraControlType = (typeof IC_LORA_CONTROL_TYPES)[number];
export type IcLoraDriveData = "none" | "visual" | "audio";
export type IcLoraDriveMediaKind = "image" | "video" | "audio";
export type ReferenceFraming = (typeof REFERENCE_FRAMINGS)[number];

/** One in-context LoRA guide. */
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
    /** Cache only; stage-0 model identity owns behavior. */
    architectureHint: VideoArchitectureId;
    modelProfileId: ModelProfileId;
    skipped: boolean;
    hue: number;
    boundaryOut: BoundaryOut;
    boundaryOutCarryAudio: boolean;
    boundaryOutReferenceScale: number;
    boundaryOutReferenceIncludeSoundtrack: boolean;
    boundaryOutOverlap: number;
    duration: number;
    refFraming: ReferenceFraming;
    audioSource: string;
    loras: ClipLora[];
    icLoras: IcLora[];
    saveAudioTrack: boolean;
    clipLengthFromAudio: boolean;
    clipLengthFromControlNet: boolean;
    reuseAudio: boolean;
    uploadedAudio: UploadedMedia | null;
    uploadedAudioDurationSeconds: number;
    uploadedAudioStartSeconds: number;
    uploadedAudioLengthSeconds: number;
    prompt: string;
    promptWindows: PromptWindow[];
    retake: Retake | null;
    initVideo: InitVideo | null;
    references: ClipReference[];
    frameRefs: FrameRefImage[];
    stages: Stage[];
}

export interface AudioTrackSource {
    kind: AudioTrackSourceKind;
    reference: string;
    uploadedAudio: UploadedMedia | null;
    mediaDurationSeconds?: number;
}

/** Root timeline window plus its source offset. */
export interface AudioTrackSpan {
    id?: string;
    timelineStartSeconds: number | null;
    timelineLengthSeconds: number | null;
    sourceStartSeconds: number;
}

/** Legacy multi-span tracks are normalized into independent lanes. */
export interface AudioTrack {
    id?: string;
    source: AudioTrackSource;
    spans: AudioTrackSpan[];
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
export type CanonicalAuthoringDocument = Omit<
    AuthoringDocument,
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

export interface ImageSourceOption {
    value: string;
    label: string;
    disabled?: boolean;
}

export type { TimelineSelection } from "./selectionTypes";
