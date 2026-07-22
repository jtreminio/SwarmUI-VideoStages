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
 * `lengthSeconds` is how long the piece plays. All seconds, 0.1 step, clamped
 * inside the clip. Backend mixes each segment additively over the base audio.
 */
export interface AudioSegment {
    source: UploadedAudio | string | null;
    startSeconds: number;
    trimStartSeconds: number;
    lengthSeconds: number;
}

export interface Stage {
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
    loras: StageLora[];
}

export interface PromptWindow {
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
    startSeconds: number;
    lengthSeconds: number;
    strength: number;
}

export interface RefImage {
    source: string;
    uploadFileName: string | null;
    uploadedImage: UploadedAudio | null;
    frame: number;
    fromEnd: boolean;
}

export type BoundaryOut = "cut" | "continue" | "crossfade";

export type IcLoraControlType = "none" | "canny" | "depth" | "normal";

/**
 * One in-context LoRA on a clip. `lora` is the LoRA model name; `preset` is a
 * curated-catalog id ("custom" = none, guidance only — never sent to the
 * backend as behavior); `strength` is the LoRA model strength; an
 * `attentionStrength` below 1 switches the backend to the Advanced guide node
 * (per-guide self-attention influence); `controlType` renders the drive video
 * into a control signal before guiding; `video` is the uploaded drive video
 * (data URI + name). An entry with no video still applies the LoRA to the
 * model (loader-only — e.g. HDR). `source` is "Upload" for per-entry uploads
 * or "Stage Input" for the frames entering the target stage (requires
 * `stage` >= 1); legacy JSON may carry "ControlNet N" captured-branch sources,
 * preserved but not authorable in the UI. `stage` restricts the entry to one
 * stage index (-1 = every stage).
 */
export interface IcLora {
    lora: string;
    preset: string;
    source: string;
    stage: number;
    strength: number;
    attentionStrength: number;
    controlType: IcLoraControlType;
    video: UploadedAudio | null;
    /**
     * Use the uploaded drive video's own audio as the clip's voice-reference
     * sample (LTXVSetAudioRefTokens) — the official LipDub one-file flow. The
     * model then GENERATES new speech matching the prompt in that voice.
     */
    driveAudioRef: boolean;
}

export interface Clip {
    skipped: boolean;
    hue: number;
    boundaryOut: BoundaryOut;
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
    uploadedAudio: UploadedAudio | null;
    audioSegments: AudioSegment[];
    prompt: string;
    promptWindows: PromptWindow[];
    retake: Retake | null;
    refs: RefImage[];
    stages: Stage[];
}

/**
 * Canonical lists of the fields each `Stored*` type persists. These are the
 * single source of truth: the `Stored*` types below are derived from them, and
 * `serializeStorageGuard.test.ts` round-trips them field-by-field. The
 * `satisfies` clause rejects any listed key that is not a real field, and the
 * `AssertClassified` aliases below reject any real field that is not listed —
 * so adding a persisted field to `Clip`/`Stage`/`RefImage` fails to compile
 * until it is classified as either stored here or intentionally carried
 * elsewhere (`UNSTORED_CLIP_KEYS`).
 */
export const STORED_REF_KEYS = [
    "source",
    "uploadFileName",
    "uploadedImage",
    "frame",
    "fromEnd",
] as const satisfies readonly (keyof RefImage)[];

export const STORED_STAGE_KEYS = [
    "skipped",
    "control",
    "controlNetStrength",
    "refStrengths",
    "upscale",
    "upscaleMethod",
    "model",
    "steps",
    "cfgScale",
    "sampler",
    "scheduler",
    "loras",
] as const satisfies readonly (keyof Stage)[];

export const STORED_CLIP_KEYS = [
    "skipped",
    "boundaryOut",
    "boundaryOutOverlap",
    "duration",
    "audioSource",
    "icLoras",
    "saveAudioTrack",
    "clipLengthFromAudio",
    "clipLengthFromControlNet",
    "reuseAudio",
    "uploadedAudio",
    "audioSegments",
    "retake",
    "refs",
    "stages",
] as const satisfies readonly (keyof Clip)[];

/**
 * Clip fields deliberately NOT in the Data param: `prompt`/`promptWindows` ride
 * the prompt carrier, `hue` rides the UI-state store. Listing them here is what
 * keeps them out of the exhaustiveness error while still forcing a new Clip
 * field to be a conscious choice between stored and carried-elsewhere.
 */
export const UNSTORED_CLIP_KEYS = [
    "hue",
    "prompt",
    "promptWindows",
] as const satisfies readonly (keyof Clip)[];

/** never when U covers every key of T, else the offending keys (a compile error). */
type AssertClassified<T, U extends keyof T> = [Exclude<keyof T, U>] extends [
    never,
]
    ? true
    : Exclude<keyof T, U>;

/**
 * Every field of these three interfaces must be accounted for. A new field
 * that is neither stored nor (for Clip) explicitly unstored turns the matching
 * alias into a non-`true` type, and the `true` assignment below stops the build.
 */
const _refKeysExhaustive: AssertClassified<
    RefImage,
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

export type StoredRefImage = Pick<RefImage, (typeof STORED_REF_KEYS)[number]>;

export type StoredStage = Pick<Stage, (typeof STORED_STAGE_KEYS)[number]>;

export type StoredClip = Pick<
    Clip,
    Exclude<(typeof STORED_CLIP_KEYS)[number], "refs" | "stages">
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

export type TimelineSelection =
    | { kind: "none" }
    | { kind: "clip"; clipIdx: number; stageIdx: number }
    | { kind: "ref"; clipIdx: number; refIdx: number }
    | { kind: "audio"; clipIdx: number }
    | { kind: "audio-segment"; clipIdx: number; segIdx: number }
    | { kind: "prompt-major"; clipIdx: number }
    | { kind: "prompt-minor"; clipIdx: number; windowIdx: number }
    | { kind: "retake"; clipIdx: number }
    | { kind: "boundary"; leftClipIdx: number };
