import {
    AUDIO_SOURCE_NATIVE,
    buildAudioSourceOptions,
    resolveAudioSourceValue,
} from "./audioSource";
import {
    CLIP_DURATION_MIN,
    clamp,
    DEFAULT_CLIP_DURATION_SECONDS,
    normalizeUploadFileName,
    REF_FRAME_MIN,
    ROOT_DIMENSION_MIN,
    ROOT_FPS_MIN,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
} from "./constants";
import { framesForClip, snapDurationToFps } from "./renderUtils";
import {
    type Clip,
    REF_SOURCE_BASE,
    type RefImage,
    type RootDefaults,
    type Stage,
    type UploadedAudio,
} from "./types";
import { utils } from "./utils";

const resolveRootPreferredUpscaleMethod = (
    upscaleMethodValues: string[],
): string =>
    upscaleMethodValues.includes("pixel-lanczos")
        ? "pixel-lanczos"
        : (upscaleMethodValues[0] ?? "pixel-lanczos");

const resolveFirstStageControl = (defaults: RootDefaults): number =>
    clamp(defaults.control, defaults.controlMin, defaults.controlMax);

const isRecord = (value: unknown): value is Record<string, unknown> =>
    typeof value === "object" && value !== null && !Array.isArray(value);

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
): number =>
    Math.max(
        ROOT_DIMENSION_MIN,
        Math.round(utils.toNumber(`${value ?? fallback}`, fallback)),
    );

export const normalizeRootFps = (value: unknown, fallback: number): number =>
    Math.max(
        ROOT_FPS_MIN,
        Math.round(utils.toNumber(`${value ?? fallback}`, fallback)),
    );

export const normalizeStageRefStrengthValue = (value: unknown): number =>
    Math.round(
        clamp(
            utils.toNumber(
                `${value ?? STAGE_REF_STRENGTH_DEFAULT}`,
                STAGE_REF_STRENGTH_DEFAULT,
            ),
            STAGE_REF_STRENGTH_MIN,
            STAGE_REF_STRENGTH_MAX,
        ) * 10,
    ) / 10;

export const buildDefaultStageRefStrengths = (refCount: number): number[] => {
    const strengths: number[] = [];
    for (let i = 0; i < refCount; i++) {
        strengths.push(STAGE_REF_STRENGTH_DEFAULT);
    }
    return strengths;
};

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

export const buildDefaultRef = (): RefImage => ({
    expanded: true,
    source: REF_SOURCE_BASE,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false,
});

export const buildDefaultClip = (
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
): Clip => {
    const defaults = getRootDefaults();
    return {
        expanded: true,
        skipped: false,
        duration: snapDurationToFps(
            Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
            defaults.fps,
        ),
        audioSource: AUDIO_SOURCE_NATIVE,
        saveAudioTrack: false,
        uploadedAudio: null,
        refs: [],
        stages: [
            buildDefaultStage(getRootDefaults, getDefaultStageModel, null, 0),
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
    const firstStageUpscale =
        stageIndexInClip === 0
            ? {
                  upscale: defaults.upscale,
                  upscaleMethod: resolveRootPreferredUpscaleMethod(
                      defaults.upscaleMethodValues,
                  ),
              }
            : {
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
    const control =
        stageIndexInClip === 0
            ? resolveFirstStageControl(defaults)
            : clamp(
                  utils.toNumber(
                      `${readRawStageProp(rawStage, "control", "Control") ?? fallback.control}`,
                      fallback.control,
                  ),
                  defaults.controlMin,
                  defaults.controlMax,
              );
    const stage: Stage = {
        expanded: rawStage.expanded === undefined ? true : !!rawStage.expanded,
        skipped: !!rawStage.skipped,
        control,
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
        expanded: rawRef.expanded === undefined ? true : !!rawRef.expanded,
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
    const refs = refsRaw.map((rawRef) =>
        normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax),
    );

    const stages: Stage[] = [];
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];

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
    return {
        expanded: rawClip.expanded === undefined ? true : !!rawClip.expanded,
        skipped: !!rawClip.skipped,
        duration,
        audioSource: resolveAudioSourceValue(
            rawAudioSource,
            audioSourceOptions,
        ),
        saveAudioTrack: !!rawClip.saveAudioTrack,
        uploadedAudio: normalizeUploadedAudio(rawClip.uploadedAudio),
        refs,
        stages,
    };
};
