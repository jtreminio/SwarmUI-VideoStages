import type {
    CanonicalIcLora,
    CanonicalRetake,
    Clip,
    FrameRefImage,
    Stage,
} from "./types";

/**
 * Canonical lists of the fields each stored type persists. Exhaustiveness
 * assertions force every new domain field to be classified explicitly.
 */
export const STORED_REF_KEYS = [
    "id",
    "source",
    "uploadFileName",
    "uploadedImage",
    "frame",
    "fromEnd",
] as const satisfies readonly (keyof FrameRefImage)[];

export const STORED_STAGE_KEYS = [
    "id",
    "skipped",
    "control",
    "controlNetStrength",
    "icLoraStrengths",
    "loraWeights",
    "frameRefStrengths",
    "upscale",
    "upscaleMethod",
    "model",
    "modelProfileId",
    "steps",
    "cfgScale",
    "sampler",
    "scheduler",
] as const satisfies readonly (keyof Stage)[];

export const STORED_CLIP_KEYS = [
    "id",
    "architectureHint",
    "modelProfileId",
    "skipped",
    "boundaryOut",
    "boundaryOutCarryAudio",
    "boundaryOutOverlap",
    "duration",
    "refFraming",
    "audioSource",
    "loras",
    "icLoras",
    "saveAudioTrack",
    "clipLengthFromAudio",
    "clipLengthFromControlNet",
    "reuseAudio",
    "uploadedAudio",
    "retake",
    "initVideo",
    "frameRefs",
    "stages",
] as const satisfies readonly (keyof Clip)[];

/** Prompt/UI fields are persisted in their dedicated carriers. */
export const UNSTORED_CLIP_KEYS = [
    "hue",
    "prompt",
    "promptWindows",
] as const satisfies readonly (keyof Clip)[];

type AssertClassified<T, U extends keyof T> = [Exclude<keyof T, U>] extends [
    never,
]
    ? true
    : Exclude<keyof T, U>;

const _refKeysExhaustive: AssertClassified<
    FrameRefImage,
    (typeof STORED_REF_KEYS)[number]
> = true;
const _stageKeysExhaustive: AssertClassified<
    Stage,
    (typeof STORED_STAGE_KEYS)[number]
> = true;
const _clipKeysExhaustive: AssertClassified<
    Clip,
    (typeof STORED_CLIP_KEYS)[number] | (typeof UNSTORED_CLIP_KEYS)[number]
> = true;
void [_refKeysExhaustive, _stageKeysExhaustive, _clipKeysExhaustive];

type RequireEntityId<T extends { id?: string }> = Omit<T, "id"> & {
    id: string;
};

export type StoredFrameRefImage = RequireEntityId<
    Pick<FrameRefImage, (typeof STORED_REF_KEYS)[number]>
>;

export type StoredStage = RequireEntityId<
    Pick<Stage, (typeof STORED_STAGE_KEYS)[number]>
>;

export type StoredClip = RequireEntityId<
    Pick<
        Clip,
        Exclude<
            (typeof STORED_CLIP_KEYS)[number],
            "icLoras" | "retake" | "frameRefs" | "stages"
        >
    >
> & {
    icLoras: CanonicalIcLora[];
    retake: CanonicalRetake | null;
    frameRefs: StoredFrameRefImage[];
    stages: StoredStage[];
};
