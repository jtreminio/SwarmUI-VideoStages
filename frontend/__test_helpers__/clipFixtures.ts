import {
    type Clip,
    REF_SOURCE_BASE,
    type RefImage,
    type Stage,
} from "../types";

export const minimalStage = (overrides: Partial<Stage> = {}): Stage => ({
    expanded: true,
    skipped: false,
    control: 1,
    controlNetStrength: 0.8,
    refStrengths: [],
    upscale: 1,
    upscaleMethod: "latentmodel-test.safetensors",
    model: "m",
    steps: 8,
    cfgScale: 1,
    sampler: "euler",
    scheduler: "normal",
    loras: [],
    ...overrides,
});

export const minimalRef = (overrides: Partial<RefImage> = {}): RefImage => ({
    expanded: true,
    source: REF_SOURCE_BASE,
    uploadFileName: null,
    uploadedImage: null,
    frame: 0,
    fromEnd: false,
    ...overrides,
});

export const minimalClip = (overrides: Partial<Clip> = {}): Clip => ({
    expanded: true,
    skipped: false,
    hue: 210,
    boundaryOut: "cut",
    duration: 2,
    audioSource: "Native",
    icLoras: [],
    saveAudioTrack: false,
    clipLengthFromAudio: false,
    clipLengthFromControlNet: false,
    reuseAudio: false,
    uploadedAudio: null,
    audioSegments: [],
    prompt: "",
    promptWindows: [],
    retake: null,
    refs: [],
    stages: [minimalStage()],
    ...overrides,
});
