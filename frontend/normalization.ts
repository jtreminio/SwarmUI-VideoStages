import {
    AUDIO_SOURCE_NATIVE,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
    resolveAudioSourceValue,
} from "./audioSource";
import {
    CONTINUE_OVERLAP_MAX_FRAMES,
    DEFAULT_CONTINUE_OVERLAP_FRAMES,
} from "./boundaryPlan";
import { normalizeStoredHue, UNASSIGNED_HUE } from "./clipColor";
import {
    AUDIO_SEGMENT_MIN_LENGTH,
    CLIP_DURATION_MIN,
    CONTROLNET_SOURCE_OPTIONS,
    clamp,
    DEFAULT_CLIP_DURATION_SECONDS,
    IC_LORA_ATTENTION_DEFAULT,
    IC_LORA_ATTENTION_MAX,
    IC_LORA_ATTENTION_MIN,
    IC_LORA_ATTENTION_STEP,
    IC_LORA_SOURCE_STAGE_INPUT,
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STAGE_ALL,
    IC_LORA_STRENGTH_DEFAULT,
    IC_LORA_STRENGTH_MAX,
    IC_LORA_STRENGTH_MIN,
    IC_LORA_STRENGTH_STEP,
    IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH,
    normalizeUploadFileName,
    REF_FRAME_MIN,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
    RETAKE_STRENGTH_MAX,
    RETAKE_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_DEFAULT,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "./constants";
import { utils } from "./hostDom";
import { IC_LORA_PRESET_CUSTOM_ID } from "./icLoraPresets";
import { framesForClip, snapDurationToFps } from "./renderUtils";
import {
    type AudioSegment,
    type BoundaryOut,
    type Clip,
    type IcLora,
    type IcLoraControlType,
    type PromptWindow,
    REF_SOURCE_REFINER,
    type RefImage,
    type Retake,
    type RootDefaults,
    type SourceVideo,
    type Stage,
    type StageLora,
    type UploadedAudio,
} from "./types";
import { isRecord, roundToTenth } from "./utils";

export const readProp = (
    raw: Record<string, unknown>,
    ...keys: string[]
): unknown => {
    for (const key of keys) {
        if (Object.hasOwn(raw, key)) {
            return raw[key];
        }
    }
    return undefined;
};

const normalizePromptWindow = (
    raw: Record<string, unknown>,
): PromptWindow | null => {
    const duration = utils.toNumber(
        `${readProp(raw, "duration", "Duration") ?? 0}`,
        0,
    );
    if (!(duration > 0)) {
        return null;
    }
    const start = Math.max(
        0,
        utils.toNumber(`${readProp(raw, "start", "Start") ?? 0}`, 0),
    );
    return {
        prompt: `${readProp(raw, "prompt", "Prompt", "text", "Text") ?? ""}`,
        start,
        duration,
    };
};

export const normalizePromptWindows = (
    rawClip: Record<string, unknown>,
): PromptWindow[] => {
    const rawList = readProp(rawClip, "promptWindows", "PromptWindows");
    if (!Array.isArray(rawList)) {
        return [];
    }
    return rawList
        .map((entry) => normalizePromptWindow(isRecord(entry) ? entry : {}))
        .filter((window): window is PromptWindow => window !== null)
        .sort((a, b) => a.start - b.start);
};

const snapToStep = (value: number, step: number): number =>
    step > 0 ? Math.round(value / step) * step : value;

/**
 * Clamp a (start, length) window inside [0, clipDuration] with a minimum
 * length: start is clamped so at least minLength fits, then length is clamped
 * to what remains. Returns null when no positive-length window survives.
 */
const clampWindowInDuration = (
    startRaw: number,
    lengthRaw: number,
    clipDuration: number,
    minLength: number,
): { startSeconds: number; lengthSeconds: number } | null => {
    if (!(lengthRaw > 0)) {
        return null;
    }
    const maxStart = Math.max(0, clipDuration - minLength);
    const startSeconds = clamp(startRaw, 0, maxStart);
    const lengthSeconds = clamp(
        lengthRaw,
        minLength,
        Math.max(minLength, clipDuration - startSeconds),
    );
    if (!(lengthSeconds > 0)) {
        return null;
    }
    return { startSeconds, lengthSeconds };
};

export const normalizeRetake = (
    value: unknown,
    clipDuration: number,
): Retake | null => {
    if (!isRecord(value)) {
        return null;
    }
    const startRaw = Math.max(
        0,
        utils.toNumber(
            `${readProp(value, "startSeconds", "StartSeconds") ?? 0}`,
            0,
        ),
    );
    const lengthRaw = utils.toNumber(
        `${readProp(value, "lengthSeconds", "LengthSeconds") ?? 0}`,
        0,
    );
    const window = clampWindowInDuration(
        startRaw,
        lengthRaw,
        clipDuration,
        RETAKE_MIN_DURATION,
    );
    if (!window) {
        return null;
    }
    const strengthRaw = readProp(value, "strength", "Strength");
    const strength =
        strengthRaw == null
            ? RETAKE_STRENGTH_DEFAULT
            : clamp(
                  utils.toNumber(`${strengthRaw}`, RETAKE_STRENGTH_DEFAULT),
                  RETAKE_STRENGTH_MIN,
                  RETAKE_STRENGTH_MAX,
              );
    return {
        startSeconds: roundToTenth(window.startSeconds),
        lengthSeconds: roundToTenth(window.lengthSeconds),
        strength,
    };
};

const resolveRootPreferredUpscaleMethod = (
    upscaleMethodValues: string[],
): string =>
    upscaleMethodValues.find((value) =>
        value.trim().toLowerCase().startsWith("latentmodel-"),
    ) ??
    upscaleMethodValues[0] ??
    "";

const snapStrengthToStep = (
    value: unknown,
    fallback: number,
    min: number,
    max: number,
    step: number,
): number => {
    const unitScale = 1 / step;
    return (
        Math.round(
            clamp(utils.toNumber(`${value ?? fallback}`, fallback), min, max) *
                unitScale,
        ) / unitScale
    );
};

/**
 * A stored source video needs at least a data blob and a positive used-range
 * length. The range is clamped inside the probed file duration when one is
 * known; unknown metadata (fps/duration 0) is preserved — the backend detects
 * fps at runtime, so a failed probe still produces a usable clip.
 */
export const normalizeSourceVideo = (value: unknown): SourceVideo | null => {
    if (!isRecord(value)) {
        return null;
    }
    const data = `${value.data ?? ""}`.trim();
    if (!data) {
        return null;
    }
    const nonNegative = (raw: unknown): number =>
        Math.max(0, utils.toNumber(`${raw ?? 0}`, 0));
    const durationSeconds = nonNegative(
        readProp(value, "durationSeconds", "DurationSeconds"),
    );
    let startSeconds = nonNegative(
        readProp(value, "startSeconds", "StartSeconds"),
    );
    let lengthSeconds = nonNegative(
        readProp(value, "lengthSeconds", "LengthSeconds"),
    );
    if (durationSeconds > 0) {
        startSeconds = Math.min(
            startSeconds,
            Math.max(0, durationSeconds - CLIP_DURATION_MIN),
        );
        if (!(lengthSeconds > 0)) {
            lengthSeconds = durationSeconds - startSeconds;
        }
        lengthSeconds = Math.min(lengthSeconds, durationSeconds - startSeconds);
    }
    if (!(lengthSeconds > 0)) {
        return null;
    }
    return {
        data,
        fileName: normalizeUploadFileName(
            value.fileName == null ? null : `${value.fileName}`,
        ),
        fps: nonNegative(readProp(value, "fps", "Fps")),
        durationSeconds: roundToTenth(durationSeconds),
        startSeconds: roundToTenth(startSeconds),
        lengthSeconds: roundToTenth(lengthSeconds),
    };
};

export const normalizeUploadedAudio = (
    value: unknown,
): UploadedAudio | null => {
    if (!isRecord(value)) {
        return null;
    }
    const data = `${value.data ?? ""}`.trim();
    if (!data) {
        return null;
    }
    return {
        data,
        fileName: normalizeUploadFileName(
            value.fileName == null ? null : `${value.fileName}`,
        ),
    };
};

const normalizeAudioSegment = (
    value: unknown,
    clipDuration: number,
): AudioSegment | null => {
    if (!isRecord(value)) {
        return null;
    }
    // A sourceless segment is kept in the working state so the "+ Add segment"
    // flow can create it and then prompt for the upload; the backend parser
    // drops segments with no source at generation time. A string source is an
    // AceStepFun track ref ("audio0", …).
    const rawSource = readProp(value, "source", "Source");
    const source =
        typeof rawSource === "string" && isAceStepFunAudioSource(rawSource)
            ? rawSource.trim()
            : normalizeUploadedAudio(rawSource);
    const startRaw = Math.max(
        0,
        utils.toNumber(
            `${readProp(value, "startSeconds", "StartSeconds") ?? 0}`,
            0,
        ),
    );
    const trimStartRaw = Math.max(
        0,
        utils.toNumber(
            `${readProp(value, "trimStartSeconds", "TrimStartSeconds") ?? 0}`,
            0,
        ),
    );
    const lengthRaw = utils.toNumber(
        `${readProp(value, "lengthSeconds", "LengthSeconds") ?? 0}`,
        0,
    );
    const window = clampWindowInDuration(
        startRaw,
        lengthRaw,
        clipDuration,
        AUDIO_SEGMENT_MIN_LENGTH,
    );
    if (!window) {
        return null;
    }
    return {
        source,
        startSeconds: roundToTenth(window.startSeconds),
        trimStartSeconds: roundToTenth(trimStartRaw),
        lengthSeconds: roundToTenth(window.lengthSeconds),
    };
};

/**
 * Normalizes the optional per-clip audio segment list against the clip
 * duration: drops entries with no upload source or non-positive length and
 * clamps each inside the clip. Array ORDER is preserved — the index is the
 * segment's timeline lane, and lanes must not reshuffle as segments move.
 * Returns [] when absent.
 */
export const normalizeAudioSegments = (
    value: unknown,
    clipDuration: number,
): AudioSegment[] => {
    if (!Array.isArray(value)) {
        return [];
    }
    return value
        .map((raw) => normalizeAudioSegment(raw, clipDuration))
        .filter((seg): seg is AudioSegment => seg !== null);
};

export const normalizeBoundaryOut = (value: unknown): BoundaryOut => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
    return raw === "continue" || raw === "crossfade" ? raw : "cut";
};

// Mirrors the backend NormalizeBoundaryOutOverlap: multiple of 8 in
// [DEFAULT_CONTINUE_OVERLAP_FRAMES, CONTINUE_OVERLAP_MAX_FRAMES], snapped down.
export const normalizeContinueOverlap = (value: unknown): number => {
    const num = Math.trunc(Number(value));
    if (!Number.isFinite(num) || num < DEFAULT_CONTINUE_OVERLAP_FRAMES) {
        return DEFAULT_CONTINUE_OVERLAP_FRAMES;
    }
    return Math.min(CONTINUE_OVERLAP_MAX_FRAMES, num - (num % 8));
};

export const normalizeControlNetSource = (value: unknown): string => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    for (const option of CONTROLNET_SOURCE_OPTIONS) {
        if (option.replace(/\s+/g, "").toLowerCase() === compact) {
            return option;
        }
    }
    return CONTROLNET_SOURCE_OPTIONS[0];
};

export const defaultIcLora = (overrides: Partial<IcLora> = {}): IcLora => ({
    lora: "",
    preset: IC_LORA_PRESET_CUSTOM_ID,
    source: IC_LORA_SOURCE_UPLOAD,
    stage: IC_LORA_STAGE_ALL,
    strength: IC_LORA_STRENGTH_DEFAULT,
    attentionStrength: IC_LORA_ATTENTION_DEFAULT,
    controlType: "none",
    video: null,
    driveAudioRef: false,
    ...overrides,
});

export const normalizeControlNetLora = (value: unknown): string => {
    const raw = `${value ?? ""}`.trim();
    if (!raw) {
        return "";
    }
    const squeezed = raw.replace(/\s+/g, "").toLowerCase();
    if (squeezed === "(none)") {
        return "";
    }
    return raw;
};

export const normalizeIcLoraControlType = (
    value: unknown,
): IcLoraControlType => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
    return raw === "canny" || raw === "depth" || raw === "normal"
        ? raw
        : "none";
};

const normalizeIcLoraSource = (value: unknown): string => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    if (!compact || compact === "upload") {
        return IC_LORA_SOURCE_UPLOAD;
    }
    if (compact === "stageinput") {
        return IC_LORA_SOURCE_STAGE_INPUT;
    }
    return normalizeControlNetSource(value);
};

const normalizeIcLoraStage = (value: unknown, stageCount: number): number => {
    if (value == null || `${value}`.trim() === "") {
        return IC_LORA_STAGE_ALL;
    }
    const stage = Math.trunc(Number(value));
    if (!Number.isFinite(stage) || stage < 0) {
        return IC_LORA_STAGE_ALL;
    }
    // A target beyond the clip's stage list (stale after stage deletion) would
    // be silently skipped by every stage the backend runs — heal it to "all".
    return stageCount > 0 && stage >= stageCount ? IC_LORA_STAGE_ALL : stage;
};

export const normalizeIcLora = (
    raw: unknown,
    stageCount: number = 0,
    sourcedClip = false,
): IcLora | null => {
    if (!isRecord(raw)) {
        return null;
    }
    const lora = normalizeControlNetLora(readProp(raw, "lora", "Lora"));
    if (!lora) {
        return null;
    }
    const preset = `${readProp(raw, "preset", "Preset") ?? ""}`.trim();
    const stage = normalizeIcLoraStage(
        readProp(raw, "stage", "Stage"),
        stageCount,
    );
    let source = normalizeIcLoraSource(readProp(raw, "source", "Source"));
    // Stage Input means "this stage's incoming frames" — meaningless below a refine-stage
    // target UNLESS the clip is sourced, where stage 0's input is the clip's footage.
    if (source === IC_LORA_SOURCE_STAGE_INPUT && stage < 1 && !sourcedClip) {
        source = IC_LORA_SOURCE_UPLOAD;
    }
    return {
        lora,
        preset: preset || IC_LORA_PRESET_CUSTOM_ID,
        source,
        stage,
        strength: snapStrengthToStep(
            readProp(raw, "strength", "Strength"),
            IC_LORA_STRENGTH_DEFAULT,
            IC_LORA_STRENGTH_MIN,
            IC_LORA_STRENGTH_MAX,
            IC_LORA_STRENGTH_STEP,
        ),
        attentionStrength: snapStrengthToStep(
            readProp(raw, "attentionStrength", "AttentionStrength"),
            IC_LORA_ATTENTION_DEFAULT,
            IC_LORA_ATTENTION_MIN,
            IC_LORA_ATTENTION_MAX,
            IC_LORA_ATTENTION_STEP,
        ),
        controlType: normalizeIcLoraControlType(
            readProp(raw, "controlType", "ControlType"),
        ),
        video: normalizeUploadedAudio(readProp(raw, "video", "Video")),
        driveAudioRef: readProp(raw, "driveAudioRef", "DriveAudioRef") === true,
    };
};

/**
 * Reads the clip's IC-LoRA list, falling back to the legacy single-entry
 * `controlNetLora` + `controlNetSource` fields when no array is present.
 */
export const normalizeIcLoras = (
    rawClip: Record<string, unknown>,
    stageCount: number = 0,
    sourcedClip = false,
): IcLora[] => {
    const raw = readProp(rawClip, "icLoras", "IcLoras");
    if (Array.isArray(raw)) {
        const entries = raw
            .map((entry) => normalizeIcLora(entry, stageCount, sourcedClip))
            .filter((entry): entry is IcLora => entry !== null);
        if (entries.length > 0) {
            return entries;
        }
    }
    const legacyLora = normalizeControlNetLora(
        readProp(rawClip, "controlNetLora", "ControlNetLora"),
    );
    if (!legacyLora) {
        return [];
    }
    return [
        defaultIcLora({
            lora: legacyLora,
            source: normalizeControlNetSource(
                readProp(rawClip, "controlNetSource", "ControlNetSource"),
            ),
        }),
    ];
};

/**
 * Heals an IC-LoRA entry after its stage target changes (a stage delete/shift or
 * a re-target): "Stage Input" media only exists on a refine stage — or anywhere
 * on a sourced clip, whose stage 0 input is the footage — so an entry that
 * dropped below stage 1 on an unsourced clip falls its source back to Upload.
 * Mutates in place.
 */
export const reconcileIcLoraStage = (
    entry: IcLora,
    sourcedClip = false,
): void => {
    if (
        entry.stage < 1 &&
        entry.source === IC_LORA_SOURCE_STAGE_INPUT &&
        !sourcedClip
    ) {
        entry.source = IC_LORA_SOURCE_UPLOAD;
    }
};

/** True when any entry is driven by a captured core "ControlNet N" branch. */
export const hasSlotSourcedIcLora = (icLoras: IcLora[]): boolean =>
    icLoras.some(
        (entry) =>
            entry.source !== IC_LORA_SOURCE_UPLOAD &&
            entry.source !== IC_LORA_SOURCE_STAGE_INPUT,
    );

export const normalizeStageRefStrengthValue = (value: unknown): number =>
    snapStrengthToStep(
        value,
        STAGE_REF_STRENGTH_DEFAULT,
        STAGE_REF_STRENGTH_MIN,
        STAGE_REF_STRENGTH_MAX,
        STAGE_REF_STRENGTH_STEP,
    );

export const normalizeStageControlNetStrengthValue = (value: unknown): number =>
    snapStrengthToStep(
        value,
        STAGE_CONTROLNET_STRENGTH_DEFAULT,
        STAGE_CONTROLNET_STRENGTH_MIN,
        STAGE_CONTROLNET_STRENGTH_MAX,
        STAGE_CONTROLNET_STRENGTH_STEP,
    );

export const buildDefaultStageRefStrengths = (
    refCount: number,
    defaultStrength = STAGE_REF_STRENGTH_DEFAULT,
): number[] => Array.from({ length: refCount }, () => defaultStrength);

export const normalizeStageRefStrengths = (
    rawStrengths: unknown,
    refCount: number,
): number[] => {
    const strengths: number[] = [];
    const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
    for (let i = 0; i < refCount; i++) {
        strengths.push(normalizeStageRefStrengthValue(rawValues[i]));
    }
    return strengths;
};

export const readRawStageProp = (
    raw: Record<string, unknown>,
    camel: string,
    pascal: string,
): unknown => readProp(raw, camel, pascal);

export const readRawStageString = (
    raw: Record<string, unknown>,
    camel: string,
    pascal: string,
): string | undefined => {
    const v = readRawStageProp(raw, camel, pascal);
    if (v == null) {
        return undefined;
    }
    const s = `${v}`.trim();
    return s.length > 0 ? s : undefined;
};

export const normalizeStageLoras = (raw: unknown): StageLora[] => {
    if (!Array.isArray(raw)) {
        return [];
    }
    const out: StageLora[] = [];
    for (const entry of raw) {
        if (!isRecord(entry)) {
            continue;
        }
        const name = `${readRawStageProp(entry, "name", "Name") ?? ""}`.trim();
        if (!name) {
            continue;
        }
        const weightRaw = readRawStageProp(entry, "weight", "Weight");
        const weight = utils.toNumber(`${weightRaw ?? 1}`, 1);
        out.push({ name, weight: Number.isFinite(weight) ? weight : 1 });
    }
    return out;
};

const cloneStageLoras = (loras: StageLora[]): StageLora[] =>
    loras.map((lora) => ({ name: lora.name, weight: lora.weight }));

export const buildDefaultStage = (
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    previousStage: Stage | null,
    refCount: number,
): Stage => {
    const defaults = getRootDefaults();
    return {
        skipped: false,
        control: previousStage ? previousStage.control : defaults.control,
        controlNetStrength: previousStage
            ? previousStage.controlNetStrength
            : STAGE_CONTROLNET_STRENGTH_DEFAULT,
        refStrengths: buildDefaultStageRefStrengths(refCount),
        upscale: previousStage ? previousStage.upscale : defaults.upscale,
        upscaleMethod: previousStage
            ? previousStage.upscaleMethod
            : resolveRootPreferredUpscaleMethod(defaults.upscaleMethodValues),
        model: previousStage
            ? previousStage.model
            : getDefaultStageModel(defaults.modelValues),
        steps: previousStage ? previousStage.steps : defaults.steps,
        cfgScale: previousStage ? previousStage.cfgScale : defaults.cfgScale,
        sampler: previousStage
            ? previousStage.sampler
            : (defaults.samplerValues[0] ?? "euler"),
        scheduler: previousStage
            ? previousStage.scheduler
            : (defaults.schedulerValues[0] ?? "normal"),
        loras: previousStage ? cloneStageLoras(previousStage.loras) : [],
    };
};

export const buildDefaultRef = (
    source: string = REF_SOURCE_REFINER,
): RefImage => ({
    source,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false,
});

export const appendRefToClip = (clip: Clip, ref: RefImage): void => {
    clip.refs.push(ref);
    for (const stage of clip.stages) {
        stage.refStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
    }
};

export const removeRefAt = (clip: Clip, refIdx: number): boolean => {
    if (refIdx < 0 || refIdx >= clip.refs.length) {
        return false;
    }
    clip.refs.splice(refIdx, 1);
    for (const stage of clip.stages) {
        if (refIdx < stage.refStrengths.length) {
            stage.refStrengths.splice(refIdx, 1);
        }
    }
    return true;
};

export const buildDefaultClip = (
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    includeDefaultRef = false,
    previousClip: Clip | null = null,
): Clip => {
    const defaults = getRootDefaults();
    const refs = includeDefaultRef ? [buildDefaultRef()] : [];
    return {
        skipped: false,
        hue: UNASSIGNED_HUE,
        boundaryOut: "cut",
        boundaryOutOverlap: DEFAULT_CONTINUE_OVERLAP_FRAMES,
        duration: previousClip
            ? previousClip.duration
            : snapDurationToFps(
                  Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
                  defaults.fps,
              ),
        audioSource: AUDIO_SOURCE_NATIVE,
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
        sourceVideo: null,
        refs,
        stages: [
            {
                ...buildDefaultStage(
                    getRootDefaults,
                    getDefaultStageModel,
                    previousClip?.stages[0] ?? null,
                    refs.length,
                ),
                refStrengths: buildDefaultStageRefStrengths(
                    refs.length,
                    includeDefaultRef
                        ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH
                        : STAGE_REF_STRENGTH_DEFAULT,
                ),
            },
        ],
    };
};

export const getReferenceFrameMax = (
    getRootDefaults: () => RootDefaults,
    clip?: Pick<Clip, "duration">,
): number => {
    const defaults = getRootDefaults();
    if (clip) {
        return Math.max(
            REF_FRAME_MIN,
            framesForClip(clip.duration, defaults.fps),
        );
    }
    return Math.max(REF_FRAME_MIN, defaults.frames);
};

export const normalizeStage = (
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    rawStage: Record<string, unknown>,
    previousStage: Stage | null,
    refCount: number,
    stageIndexInClip: number,
    sourcedClip = false,
): Stage => {
    const defaults = getRootDefaults();
    const fallback = buildDefaultStage(
        getRootDefaults,
        getDefaultStageModel,
        previousStage,
        refCount,
    );
    // A generation first stage forces control/upscale; a sourced clip's stage 0
    // refines its footage (init-video img2img), so it keeps authored values.
    const forcedFirstStage = stageIndexInClip === 0 && !sourcedClip;
    let firstStageUpscale: { upscale: number; upscaleMethod: string };
    let control: number;
    if (forcedFirstStage) {
        firstStageUpscale = {
            upscale: defaults.upscale,
            upscaleMethod: resolveRootPreferredUpscaleMethod(
                defaults.upscaleMethodValues,
            ),
        };
        control = clamp(
            defaults.control,
            defaults.controlMin,
            defaults.controlMax,
        );
    } else {
        firstStageUpscale = {
            upscale: snapToStep(
                clamp(
                    utils.toNumber(
                        `${readRawStageProp(rawStage, "upscale", "Upscale") ?? fallback.upscale}`,
                        fallback.upscale,
                    ),
                    defaults.upscaleMin,
                    defaults.upscaleMax,
                ),
                defaults.upscaleStep,
            ),
            upscaleMethod:
                `${readRawStageString(rawStage, "upscaleMethod", "UpscaleMethod") ?? fallback.upscaleMethod}` ||
                fallback.upscaleMethod,
        };
        control = clamp(
            utils.toNumber(
                `${readRawStageProp(rawStage, "control", "Control") ?? fallback.control}`,
                fallback.control,
            ),
            defaults.controlMin,
            defaults.controlMax,
        );
    }
    const stage: Stage = {
        skipped: !!rawStage.skipped,
        control,
        controlNetStrength: normalizeStageControlNetStrengthValue(
            readRawStageProp(
                rawStage,
                "controlNetStrength",
                "ControlNetStrength",
            ) ?? fallback.controlNetStrength,
        ),
        refStrengths: normalizeStageRefStrengths(
            rawStage.refStrengths,
            refCount,
        ),
        upscale: firstStageUpscale.upscale,
        upscaleMethod: firstStageUpscale.upscaleMethod,
        model: `${rawStage.model ?? fallback.model}` || fallback.model,
        steps: Math.max(
            1,
            Math.round(
                clamp(
                    utils.toNumber(
                        `${rawStage.steps ?? fallback.steps}`,
                        fallback.steps,
                    ),
                    defaults.stepsMin,
                    defaults.stepsMax,
                ),
            ),
        ),
        cfgScale: clamp(
            utils.toNumber(
                `${rawStage.cfgScale ?? fallback.cfgScale}`,
                fallback.cfgScale,
            ),
            defaults.cfgScaleMin,
            defaults.cfgScaleMax,
        ),
        sampler: `${rawStage.sampler ?? fallback.sampler}` || fallback.sampler,
        scheduler:
            `${rawStage.scheduler ?? fallback.scheduler}` || fallback.scheduler,
        loras: normalizeStageLoras(
            readRawStageProp(rawStage, "loras", "Loras"),
        ),
    };

    if (
        !defaults.upscaleMethodValues.includes(stage.upscaleMethod) &&
        defaults.upscaleMethodValues.length > 0
    ) {
        stage.upscaleMethod = forcedFirstStage
            ? (defaults.upscaleMethodValues[0] ?? "")
            : stage.upscaleMethod || fallback.upscaleMethod;
    }
    return stage;
};

export const normalizeRef = (
    rawRef: Record<string, unknown>,
    frameMax: number,
): RefImage => {
    const fallback = buildDefaultRef();
    const source = `${rawRef.source ?? fallback.source}` || fallback.source;
    const ref: RefImage = {
        source,
        uploadFileName:
            rawRef.uploadFileName == null || rawRef.uploadFileName === ""
                ? null
                : `${rawRef.uploadFileName}`,
        uploadedImage: normalizeUploadedAudio(rawRef.uploadedImage),
        frame: Math.max(
            REF_FRAME_MIN,
            Math.round(
                clamp(
                    utils.toNumber(
                        `${rawRef.frame ?? fallback.frame}`,
                        fallback.frame,
                    ),
                    REF_FRAME_MIN,
                    frameMax,
                ),
            ),
        ),
        fromEnd: !!rawRef.fromEnd,
    };
    return ref;
};

export const normalizeClip = (
    rawClip: Record<string, unknown>,
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
): Clip => {
    const defaults = getRootDefaults();
    const rawAudioSource = `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`;
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const sourceVideo = normalizeSourceVideo(
        readProp(rawClip, "sourceVideo", "SourceVideo"),
    );
    const icLoras = normalizeIcLoras(
        rawClip,
        stagesRaw.length,
        sourceVideo !== null,
    );
    const audioSourceOptions = buildAudioSourceOptions(rawAudioSource, {
        controlNetEnabled: hasSlotSourcedIcLora(icLoras),
    });
    const fps = Math.max(1, defaults.fps);
    // A sourced clip's duration IS its used source range.
    const rawDuration =
        sourceVideo?.lengthSeconds ??
        utils.toNumber(`${rawClip.duration}`, defaults.frames / fps);
    const duration = snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, rawDuration),
        fps,
    );
    const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
    const refFrameMax = getReferenceFrameMax(getRootDefaults, { duration });
    const refs = refsRaw.map((rawRef) =>
        normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax),
    );

    const stages: Stage[] = [];

    for (let i = 0; i < stagesRaw.length; i++) {
        const previousStage = i > 0 ? stages[i - 1] : null;
        stages.push(
            normalizeStage(
                getRootDefaults,
                getDefaultStageModel,
                isRecord(stagesRaw[i]) ? stagesRaw[i] : {},
                previousStage,
                refs.length,
                i,
                sourceVideo !== null,
            ),
        );
    }
    const audioSource = resolveAudioSourceValue(
        rawAudioSource,
        audioSourceOptions,
    );
    const clipLengthFromAudio =
        canUseClipLengthFromAudio(audioSource) && !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet =
        hasSlotSourcedIcLora(icLoras) &&
        !clipLengthFromAudio &&
        !!(
            rawClip.clipLengthFromControlNet ?? rawClip.ClipLengthFromControlNet
        );
    const clip: Clip = {
        skipped: !!rawClip.skipped,
        hue: normalizeStoredHue(rawClip.hue),
        boundaryOut: normalizeBoundaryOut(
            rawClip.boundaryOut ?? rawClip.BoundaryOut,
        ),
        boundaryOutOverlap: normalizeContinueOverlap(
            rawClip.boundaryOutOverlap ?? rawClip.BoundaryOutOverlap,
        ),
        duration,
        audioSource,
        icLoras,
        saveAudioTrack: !!rawClip.saveAudioTrack,
        clipLengthFromAudio,
        clipLengthFromControlNet,
        reuseAudio: !!rawClip.reuseAudio,
        uploadedAudio: normalizeUploadedAudio(rawClip.uploadedAudio),
        audioSegments: normalizeAudioSegments(
            readProp(rawClip, "audioSegments", "AudioSegments"),
            duration,
        ),
        prompt: `${readProp(rawClip, "prompt", "Prompt") ?? ""}`,
        promptWindows: normalizePromptWindows(rawClip),
        retake: normalizeRetake(
            readProp(rawClip, "retake", "Retake"),
            duration,
        ),
        sourceVideo,
        refs,
        stages,
    };
    return clip;
};
