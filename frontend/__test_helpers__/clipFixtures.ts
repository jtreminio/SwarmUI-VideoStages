import { MEDIA_SOURCE_BASE } from "../generatedMediaSource";
import type {
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan,
    CanonicalAuthoringDocument,
    CanonicalClip,
    CanonicalFrameRefImage,
    CanonicalPromptWindow,
    CanonicalRetake,
    CanonicalStage,
    Clip,
    ClipReference,
    FrameRefImage,
    IcLora,
    InitVideo,
    Stage,
} from "../types";
import { CURRENT_AUTHORING_SCHEMA_VERSION } from "../types";

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
    frameRefStrengths: [],
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

export const minimalRef = (
    overrides: Partial<FrameRefImage> = {},
): FrameRefImage => ({
    source: MEDIA_SOURCE_BASE,
    uploadFileName: null,
    uploadedImage: null,
    frame: 0,
    fromEnd: false,
    ...overrides,
});

export const minimalClipReference = (
    overrides: Partial<ClipReference> = {},
): ClipReference => ({
    kind: "image",
    source: "Upload",
    uploadedMedia: {
        data: "data:image/png;base64,QQ==",
        fileName: "subject.png",
    },
    includeSoundtrack: false,
    mediaDurationSeconds: 0,
    startSeconds: 0,
    lengthSeconds: 0,
    drivesClipLength: false,
    mediaScale: 1,
    ...overrides,
});

export const minimalClip = (overrides: Partial<Clip> = {}): Clip => ({
    architectureHint: "ltx2",
    modelProfileId: "ltx-2.3",
    skipped: false,
    hue: 210,
    boundaryOut: "cut",
    boundaryOutCarryAudio: false,
    boundaryOutReferenceScale: 1,
    boundaryOutReferenceIncludeSoundtrack: true,
    boundaryOutOverlap: 8,
    duration: 2,
    refFraming: "crop",
    h3AttentionWindowSeconds: 0,
    h3TextEncoder: "default",
    audioSource: "Native",
    loras: [],
    icLoras: [],
    saveAudioTrack: false,
    clipLengthFromAudio: false,
    clipLengthFromControlNet: false,
    reuseAudio: false,
    uploadedAudio: null,
    uploadedAudioDurationSeconds: 0,
    uploadedAudioStartSeconds: 0,
    uploadedAudioLengthSeconds: 0,
    prompt: "",
    promptWindows: [],
    retake: null,
    initVideo: null,
    references: [],
    frameRefs: [],
    stages: [minimalStage()],
    ...overrides,
});

/**
 * ID-bearing fixtures for the document command/diff tests, which address every entity by a
 * stable ID.
 */
export const canonicalStage = (
    id: string,
    overrides: Partial<CanonicalStage> = {},
): CanonicalStage => ({
    ...minimalStage({
        control: 0.5,
        controlNetStrength: 1,
        upscaleMethod: "pixel-lanczos",
        model: "ltx",
    }),
    id,
    ...overrides,
});

export const canonicalRef = (
    id: string,
    overrides: Partial<CanonicalFrameRefImage> = {},
): CanonicalFrameRefImage => ({
    ...minimalRef({ frame: 1 }),
    id,
    ...overrides,
});

export const canonicalPromptWindow = (
    id: string,
    overrides: Partial<CanonicalPromptWindow> = {},
): CanonicalPromptWindow => ({
    id,
    prompt: id,
    start: 0,
    duration: 1,
    ...overrides,
});

export const canonicalRetake = (
    id: string,
    overrides: Partial<CanonicalRetake> = {},
): CanonicalRetake => ({
    id,
    startSeconds: 0,
    lengthSeconds: 1,
    strength: 0.5,
    ...overrides,
});

export const canonicalClip = (
    id: string,
    overrides: Partial<CanonicalClip> = {},
): CanonicalClip => ({
    ...(minimalClip({ prompt: id, duration: 4, stages: [] }) as CanonicalClip),
    id,
    ...overrides,
});

export const canonicalAudioSpan = (
    id: string,
    overrides: Partial<CanonicalAudioTrackSpan> = {},
): CanonicalAudioTrackSpan => ({
    id,
    timelineStartSeconds: null,
    timelineLengthSeconds: null,
    sourceStartSeconds: 0,
    ...overrides,
});

export const canonicalAudioTrack = (
    id: string,
    overrides: Partial<CanonicalAudioTrack> = {},
): CanonicalAudioTrack => ({
    id,
    source: { kind: "Unrecognized", reference: id, uploadedAudio: null },
    spans: [],
    ...overrides,
});

export const canonicalDocument = (
    overrides: Partial<CanonicalAuthoringDocument> = {},
): CanonicalAuthoringDocument => ({
    schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
    width: 1024,
    height: 576,
    fps: 24,
    dimsExplicit: true,
    clips: [],
    audioTracks: [],
    ...overrides,
});

/**
 * The persisted wire record for a clip — the shape `storageTypes.ts` owns, not a normalized
 * {@link Clip}. Track tests feed this to the renderer so normalization fills the rest.
 */
export const storedClip = (
    overrides: Record<string, unknown> = {},
): Record<string, unknown> => ({
    duration: 2,
    stages: [{}],
    frameRefs: [],
    promptWindows: [],
    ...overrides,
});
