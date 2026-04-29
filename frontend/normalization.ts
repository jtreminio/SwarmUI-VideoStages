import {
    AUDIO_SOURCE_NATIVE,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    resolveAudioSourceValue,
} from "./audioSource";
import {
    CLIP_DURATION_MIN,
    CONTROLNET_SOURCE_OPTIONS,
    clamp,
    DEFAULT_CLIP_DURATION_SECONDS,
    IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH,
    normalizeUploadFileName,
    REF_FRAME_MIN,
    ROOT_DIMENSION_MIN,
    ROOT_FPS_MIN,
    STAGE_CONTROLNET_STRENGTH_DEFAULT,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "./constants";
import { framesForClip, snapDurationToFps } from "./renderUtils";
import {
    type Clip,
    REF_SOURCE_REFINER,
    type RefImage,
    type RootDefaults,
    type Stage,
    type UploadedAudio,
} from "./types";
import { utils } from "./utils";
import { clipHasWanStage, rawStageListContainsWanModel } from "./wanModel";

const resolveRootPreferredUpscaleMethod = (
    upscaleMethodValues: string[],
): string =>
    upscaleMethodValues.includes("pixel-lanczos")
        ? "pixel-lanczos"
        : (upscaleMethodValues[0] ?? "pixel-lanczos");

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

const normalizeExpanded = (raw: { expanded?: unknown }): boolean =>
    raw.expanded === undefined ? true : !!raw.expanded;

const normalizeRootPositiveInt = (
    value: unknown,
    fallback: number,
    min: number,
): number =>
    Math.max(min, Math.round(utils.toNumber(`${value ?? fallback}`, fallback)));

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

export const normalizeRootDimension = (
    value: unknown,
    fallback: number,
): number => normalizeRootPositiveInt(value, fallback, ROOT_DIMENSION_MIN);

export const normalizeRootFps = (value: unknown, fallback: number): number =>
    normalizeRootPositiveInt(value, fallback, ROOT_FPS_MIN);

export const normalizeControlNetSource = (value: unknown): string => {
    const compact = `${value ?? ""}`.trim().replace(/\s+/g, "").toLowerCase();
    for (const option of CONTROLNET_SOURCE_OPTIONS) {
        if (option.replace(/\s+/g, "").toLowerCase() === compact) {
            return option;
        }
    }
    return CONTROLNET_SOURCE_OPTIONS[0];
};

export const normalizeOptionalModelName = (value: unknown): string => {
    const raw = `${value ?? ""}`.trim();
    return raw || "";
};

export const normalizeControlNetLora = (value: unknown): string => {
    const raw = normalizeOptionalModelName(value);
    if (!raw) {
        return "";
    }
    const squeezed = raw.replace(/\s+/g, "").toLowerCase();
    if (squeezed === "(none)") {
        return "";
    }
    return raw;
};

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
): unknown => {
    if (Object.hasOwn(raw, camel)) {
        return raw[camel];
    }
    if (Object.hasOwn(raw, pascal)) {
        return raw[pascal];
    }
    return undefined;
};

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

export const buildDefaultStage = (
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    previousStage: Stage | null,
    refCount: number,
): Stage => {
    const defaults = getRootDefaults();
    return {
        expanded: true,
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
        vae: previousStage ? previousStage.vae : (defaults.vaeValues[0] ?? ""),
        steps: previousStage ? previousStage.steps : defaults.steps,
        cfgScale: previousStage ? previousStage.cfgScale : defaults.cfgScale,
        sampler: previousStage
            ? previousStage.sampler
            : (defaults.samplerValues[0] ?? "euler"),
        scheduler: previousStage
            ? previousStage.scheduler
            : (defaults.schedulerValues[0] ?? "normal"),
    };
};

export const buildDefaultRef = (
    source: string = REF_SOURCE_REFINER,
): RefImage => ({
    expanded: true,
    source,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false,
});

export const buildDefaultClip = (
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    includeDefaultRef = false,
): Clip => {
    const defaults = getRootDefaults();
    const refs = includeDefaultRef ? [buildDefaultRef()] : [];
    return {
        expanded: true,
        skipped: false,
        duration: snapDurationToFps(
            Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
            defaults.fps,
        ),
        audioSource: AUDIO_SOURCE_NATIVE,
        controlNetSource: CONTROLNET_SOURCE_OPTIONS[0],
        controlNetLora: "",
        saveAudioTrack: false,
        clipLengthFromAudio: false,
        clipLengthFromControlNet: false,
        reuseAudio: false,
        uploadedAudio: null,
        refs,
        stages: [
            {
                ...buildDefaultStage(
                    getRootDefaults,
                    getDefaultStageModel,
                    null,
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
): Stage => {
    const defaults = getRootDefaults();
    const fallback = buildDefaultStage(
        getRootDefaults,
        getDefaultStageModel,
        previousStage,
        refCount,
    );
    let firstStageUpscale: { upscale: number; upscaleMethod: string };
    let control: number;
    if (stageIndexInClip === 0) {
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
            upscale: clamp(
                utils.toNumber(
                    `${readRawStageProp(rawStage, "upscale", "Upscale") ?? fallback.upscale}`,
                    fallback.upscale,
                ),
                defaults.upscaleMin,
                defaults.upscaleMax,
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
        expanded: normalizeExpanded(rawStage),
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
        vae: `${rawStage.vae ?? fallback.vae ?? ""}`,
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
    };

    if (
        !defaults.upscaleMethodValues.includes(stage.upscaleMethod) &&
        defaults.upscaleMethodValues.length > 0
    ) {
        stage.upscaleMethod =
            stageIndexInClip === 0
                ? (defaults.upscaleMethodValues[0] ?? "pixel-lanczos")
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
        expanded: normalizeExpanded(rawRef),
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

export const normalizeWanClipStructuralRefs = (clip: Clip): void => {
    if (!clipHasWanStage(clip)) {
        return;
    }
    const wanStructuralRefMax = 2;
    if (clip.refs.length > wanStructuralRefMax) {
        clip.refs = clip.refs.slice(0, wanStructuralRefMax);
        for (let s = 0; s < clip.stages.length; s++) {
            clip.stages[s].refStrengths = clip.stages[s].refStrengths.slice(
                0,
                wanStructuralRefMax,
            );
        }
    }
    if (clip.refs.length > 0) {
        clip.refs[0] = {
            ...clip.refs[0],
            frame: REF_FRAME_MIN,
            fromEnd: false,
        };
    }
    if (clip.refs.length > 1) {
        clip.refs[1] = {
            ...clip.refs[1],
            frame: REF_FRAME_MIN,
            fromEnd: true,
        };
    }
};

export const normalizeClip = (
    rawClip: Record<string, unknown>,
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
): Clip => {
    const defaults = getRootDefaults();
    const rawAudioSource = `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`;
    const audioSourceOptions = buildAudioSourceOptions(rawAudioSource);
    const fps = Math.max(1, defaults.fps);
    const rawDuration = utils.toNumber(
        `${rawClip.duration}`,
        defaults.frames / fps,
    );
    const duration = snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, rawDuration),
        fps,
    );
    const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
    const refFrameMax = getReferenceFrameMax(getRootDefaults, { duration });
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const refsSource = rawStageListContainsWanModel(stagesRaw)
        ? refsRaw.slice(0, 2)
        : refsRaw;
    const refs = refsSource.map((rawRef) =>
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
            ),
        );
    }
    const audioSource = resolveAudioSourceValue(
        rawAudioSource,
        audioSourceOptions,
    );
    const controlNetLora = normalizeControlNetLora(
        rawClip.controlNetLora ?? rawClip.ControlNetLora,
    );
    const clipLengthFromAudio =
        canUseClipLengthFromAudio(audioSource) && !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet =
        controlNetLora !== "" &&
        !clipLengthFromAudio &&
        !!(
            rawClip.clipLengthFromControlNet ?? rawClip.ClipLengthFromControlNet
        );
    const clip: Clip = {
        expanded: normalizeExpanded(rawClip),
        skipped: !!rawClip.skipped,
        duration,
        audioSource,
        controlNetSource: normalizeControlNetSource(
            rawClip.controlNetSource ?? rawClip.ControlNetSource,
        ),
        controlNetLora,
        saveAudioTrack: !!rawClip.saveAudioTrack,
        clipLengthFromAudio,
        clipLengthFromControlNet,
        reuseAudio: !!rawClip.reuseAudio,
        uploadedAudio: normalizeUploadedAudio(rawClip.uploadedAudio),
        refs,
        stages,
    };
    normalizeWanClipStructuralRefs(clip);
    return clip;
};
