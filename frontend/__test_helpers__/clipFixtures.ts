import {
    type Clip,
    type IcLora,
    type InitVideo,
    REF_SOURCE_BASE,
    type RefImage,
    type Stage,
} from "../types";

export const initVideoFixture = (
    overrides: Partial<InitVideo> = {},
): InitVideo => ({
    data: "data:video/mp4;base64,AA==",
    fileName: "base.mp4",
    fps: 24,
    durationSeconds: 2,
    startSeconds: 0,
    lengthSeconds: 2,
    ...overrides,
});

export const icLoraFixture = (overrides: Partial<IcLora> = {}): IcLora => ({
    lora: "ltx-ic-lora-pose.safetensors",
    preset: "pose",
    driveSource: "Upload",
    driveData: "visual",
    driveMediaKinds: ["image", "video"],
    stage: -1,
    strength: 1,
    attentionStrength: 1,
    controlType: "none",
    driveMedia: null,
    ...overrides,
});

export const minimalStage = (overrides: Partial<Stage> = {}): Stage => ({
    skipped: false,
    control: 1,
    controlNetStrength: 0.8,
    icLoraStrengths: [],
    loraWeights: [],
    refStrengths: [],
    upscale: 1,
    upscaleMethod: "latentmodel-test.safetensors",
    model: "ltx-2.3.safetensors",
    modelProfileId: "ltx-2.3",
    steps: 8,
    cfgScale: 1,
    sampler: "euler",
    scheduler: "normal",
    ...overrides,
});

export const minimalRef = (overrides: Partial<RefImage> = {}): RefImage => ({
    source: REF_SOURCE_BASE,
    uploadFileName: null,
    uploadedImage: null,
    frame: 0,
    fromEnd: false,
    ...overrides,
});

export const minimalClip = (overrides: Partial<Clip> = {}): Clip => ({
    architectureHint: "ltx2",
    modelProfileId: "ltx-2.3",
    skipped: false,
    hue: 210,
    boundaryOut: "cut",
    boundaryOutCarryAudio: false,
    boundaryOutOverlap: 8,
    duration: 2,
    refFraming: "crop",
    audioSource: "Native",
    loras: [],
    icLoras: [],
    saveAudioTrack: false,
    clipLengthFromAudio: false,
    clipLengthFromControlNet: false,
    reuseAudio: false,
    uploadedAudio: null,
    prompt: "",
    promptWindows: [],
    retake: null,
    initVideo: null,
    refs: [],
    stages: [minimalStage()],
    ...overrides,
});
