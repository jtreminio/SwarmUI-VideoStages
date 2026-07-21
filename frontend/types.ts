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
    | "loras"
>;

export type StoredClip = Pick<
    Clip,
    | "skipped"
    | "boundaryOut"
    | "duration"
    | "audioSource"
    | "icLoras"
    | "saveAudioTrack"
    | "clipLengthFromAudio"
    | "clipLengthFromControlNet"
    | "reuseAudio"
    | "uploadedAudio"
    | "audioSegments"
    | "retake"
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

/**
 * Selection-driven state for the bottom detail strip. Phase 1 only produces
 * `none` and `clip`; the other kinds exist for the Phase 2 editors.
 */
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
