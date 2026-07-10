export interface RootDefaults {
    modelValues: string[];
    modelLabels: string[];
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
    width: number;
    height: number;
    fps: number;
    dimsExplicit: boolean;
    fpsExplicit: boolean;
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
    controlNetStrength: number;
    refStrengths: number[];
    upscale: number;
    upscaleMethod: string;
    model: string;
    steps: number;
    cfgScale: number;
    sampler: string;
    scheduler: string;
}

export interface PromptWindow {
    prompt: string;
    start: number;
    duration: number;
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
    hue: number;
    duration: number;
    audioSource: string;
    controlNetSource: string;
    controlNetLora: string;
    saveAudioTrack: boolean;
    clipLengthFromAudio: boolean;
    clipLengthFromControlNet: boolean;
    reuseAudio: boolean;
    uploadedAudio: UploadedAudio | null;
    prompt: string;
    promptWindows: PromptWindow[];
    refs: RefImage[];
    stages: Stage[];
}

export type StoredRefImage = Pick<
    RefImage,
    "source" | "uploadFileName" | "uploadedImage" | "frame" | "fromEnd"
>;

export type StoredStage = Pick<
    Stage,
    | "skipped"
    | "control"
    | "controlNetStrength"
    | "refStrengths"
    | "upscale"
    | "upscaleMethod"
    | "model"
    | "steps"
    | "cfgScale"
    | "sampler"
    | "scheduler"
>;

export type StoredClip = Pick<
    Clip,
    | "skipped"
    | "duration"
    | "audioSource"
    | "controlNetSource"
    | "controlNetLora"
    | "saveAudioTrack"
    | "clipLengthFromAudio"
    | "clipLengthFromControlNet"
    | "reuseAudio"
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
