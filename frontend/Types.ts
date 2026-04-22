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

export interface Stage {
    expanded: boolean;
    skipped: boolean;
    control: number;
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
    frame: number;
    fromEnd: boolean;
}

export interface Clip {
    name: string;
    expanded: boolean;
    skipped: boolean;
    duration: number;
    width: number;
    height: number;
    refs: RefImage[];
    stages: Stage[];
}

export const REF_SOURCE_BASE = "Base";
export const REF_SOURCE_REFINER = "Refiner";
export const REF_SOURCE_UPLOAD = "Upload";

export interface ImageSourceOption {
    value: string;
    label: string;
    disabled?: boolean;
}
