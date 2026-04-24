export interface RootDefaults {
    modelValues: string[];
    modelLabels: string[];
    vaeValues: string[];
    vaeLabels: string[];
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
    width: number;
    height: number;
    fps: number;
    clips: Clip[];
}

export interface UploadedAudio {
    data: string;
    fileName: string | null;
}

export interface Stage {
    expanded: boolean;
    skipped: boolean;
    control: number;
    refStrengths: number[];
    upscale: number;
    upscaleMethod: string;
    model: string;
    vae: string;
    steps: number;
    cfgScale: number;
    sampler: string;
    scheduler: string;
}

export interface RefImage {
    expanded: boolean;
    source: string;
    uploadFileName: string | null;
    uploadedImage: UploadedAudio | null;
    frame: number;
    fromEnd: boolean;
}

export interface Clip {
    expanded: boolean;
    skipped: boolean;
    duration: number;
    audioSource: string;
    saveAudioTrack: boolean;
    uploadedAudio: UploadedAudio | null;
    refs: RefImage[];
    stages: Stage[];
}

/** Ref fields persisted by `serializeClipsForStorage`. */
export type StoredRefImage = Pick<
    RefImage,
    | "expanded"
    | "source"
    | "uploadFileName"
    | "uploadedImage"
    | "frame"
    | "fromEnd"
>;

/** Stage fields persisted by `serializeClipsForStorage`. */
export type StoredStage = Pick<
    Stage,
    | "expanded"
    | "skipped"
    | "control"
    | "refStrengths"
    | "upscale"
    | "upscaleMethod"
    | "model"
    | "vae"
    | "steps"
    | "cfgScale"
    | "sampler"
    | "scheduler"
>;

export type StoredClip = Pick<
    Clip,
    | "expanded"
    | "skipped"
    | "duration"
    | "audioSource"
    | "saveAudioTrack"
    | "uploadedAudio"
> & {
    refs: StoredRefImage[];
    stages: StoredStage[];
};

export const REF_SOURCE_BASE = "Base";
export const REF_SOURCE_REFINER = "Refiner";
export const REF_SOURCE_UPLOAD = "Upload";

export interface ImageSourceOption {
    value: string;
    label: string;
    disabled?: boolean;
}
