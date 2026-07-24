import { modelProfileForModel } from "./architectures/catalog";
import {
    clamp,
    REF_FRAME_MIN,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "./constants";
import {
    STAGE_CONTROLNET_STRENGTH_DEFAULT,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
} from "./icLoraAuthoring";
import { normalizeUploadedMedia } from "./normalizationMedia";
import {
    clampedNumber,
    normalizeOptionalEntityId,
    numberOr,
    readProp,
    resolveRootPreferredUpscaleMethod,
    snapToStep,
    snapValueToStep,
    textOr,
    trimmedText,
} from "./normalizationShared";
import { framesForClip } from "./renderUtils";
import {
    type Clip,
    REF_SOURCE_REFINER,
    type RefImage,
    type RootDefaults,
    type Stage,
    type StageLora,
} from "./types";
import { isRecord } from "./utils";

export const normalizeStageRefStrengthValue = (value: unknown): number =>
    snapValueToStep(
        value,
        STAGE_REF_STRENGTH_DEFAULT,
        STAGE_REF_STRENGTH_MIN,
        STAGE_REF_STRENGTH_MAX,
        STAGE_REF_STRENGTH_STEP,
    );

export const normalizeStageControlNetStrengthValue = (value: unknown): number =>
    snapValueToStep(
        value,
        STAGE_CONTROLNET_STRENGTH_DEFAULT,
        STAGE_CONTROLNET_STRENGTH_MIN,
        STAGE_CONTROLNET_STRENGTH_MAX,
        STAGE_CONTROLNET_STRENGTH_STEP,
    );

export const normalizeStageIcLoraStrengths = (
    rawStrengths: unknown,
    icLoraCount: number,
    fallbackStrength = STAGE_CONTROLNET_STRENGTH_DEFAULT,
): number[] => {
    const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
    return Array.from({ length: icLoraCount }, (_, index) =>
        normalizeStageControlNetStrengthValue(
            rawValues[index] ?? fallbackStrength,
        ),
    );
};

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
    key: string,
): unknown => readProp(raw, key);

export const readRawStageString = (
    raw: Record<string, unknown>,
    key: string,
): string | undefined => {
    const value = readRawStageProp(raw, key);
    if (value == null) {
        return undefined;
    }
    return trimmedText(value) || undefined;
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
        const name = trimmedText(readRawStageProp(entry, "name"));
        if (!name) {
            continue;
        }
        out.push({
            name,
            weight: numberOr(readRawStageProp(entry, "weight"), 1),
        });
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
    const model = previousStage
        ? previousStage.model
        : getDefaultStageModel(defaults.modelValues);
    return {
        skipped: false,
        control: previousStage ? previousStage.control : defaults.control,
        controlNetStrength: previousStage
            ? previousStage.controlNetStrength
            : STAGE_CONTROLNET_STRENGTH_DEFAULT,
        icLoraStrengths: previousStage
            ? [...previousStage.icLoraStrengths]
            : [],
        refStrengths: buildDefaultStageRefStrengths(refCount),
        upscale: previousStage ? previousStage.upscale : defaults.upscale,
        upscaleMethod: previousStage
            ? previousStage.upscaleMethod
            : resolveRootPreferredUpscaleMethod(defaults.upscaleMethodValues),
        model,
        modelProfileId:
            previousStage?.modelProfileId ??
            modelProfileForModel(defaults.modelCatalog, model) ??
            "unsupported",
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

export const appendIcLoraStrengthToClip = (clip: Clip): void => {
    for (const stage of clip.stages) {
        stage.icLoraStrengths.push(STAGE_CONTROLNET_STRENGTH_DEFAULT);
    }
};

export const removeIcLoraStrengthAt = (clip: Clip, entryIdx: number): void => {
    for (const stage of clip.stages) {
        if (entryIdx < stage.icLoraStrengths.length) {
            stage.icLoraStrengths.splice(entryIdx, 1);
        }
    }
};

export const getReferenceFrameMax = (
    getRootDefaults: () => RootDefaults,
    clip?: Pick<Clip, "duration">,
    effectiveFps?: number,
): number => {
    const defaults = getRootDefaults();
    const fps =
        typeof effectiveFps === "number" &&
        Number.isFinite(effectiveFps) &&
        effectiveFps > 0
            ? effectiveFps
            : defaults.fps;
    if (clip) {
        return Math.max(REF_FRAME_MIN, framesForClip(clip.duration, fps));
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
                clampedNumber(
                    readRawStageProp(rawStage, "upscale"),
                    fallback.upscale,
                    defaults.upscaleMin,
                    defaults.upscaleMax,
                ),
                defaults.upscaleStep,
            ),
            upscaleMethod: textOr(
                readRawStageString(rawStage, "upscaleMethod"),
                fallback.upscaleMethod,
            ),
        };
        control = clampedNumber(
            readRawStageProp(rawStage, "control"),
            fallback.control,
            defaults.controlMin,
            defaults.controlMax,
        );
    }
    const stage: Stage = {
        id: normalizeOptionalEntityId(rawStage.id),
        skipped: !!rawStage.skipped,
        control,
        controlNetStrength: normalizeStageControlNetStrengthValue(
            readRawStageProp(rawStage, "controlNetStrength") ??
                fallback.controlNetStrength,
        ),
        icLoraStrengths: Array.isArray(rawStage.icLoraStrengths)
            ? rawStage.icLoraStrengths.map(
                  normalizeStageControlNetStrengthValue,
              )
            : [...fallback.icLoraStrengths],
        refStrengths: normalizeStageRefStrengths(
            rawStage.refStrengths,
            refCount,
        ),
        upscale: firstStageUpscale.upscale,
        upscaleMethod: firstStageUpscale.upscaleMethod,
        model: textOr(rawStage.model, fallback.model),
        modelProfileId: "unsupported",
        steps: Math.max(
            1,
            Math.round(
                clampedNumber(
                    rawStage.steps,
                    fallback.steps,
                    defaults.stepsMin,
                    defaults.stepsMax,
                ),
            ),
        ),
        cfgScale: clampedNumber(
            rawStage.cfgScale,
            fallback.cfgScale,
            defaults.cfgScaleMin,
            defaults.cfgScaleMax,
        ),
        sampler: textOr(rawStage.sampler, fallback.sampler),
        scheduler: textOr(rawStage.scheduler, fallback.scheduler),
        loras: normalizeStageLoras(readRawStageProp(rawStage, "loras")),
    };
    stage.modelProfileId =
        trimmedText(readRawStageProp(rawStage, "modelProfileId")) ||
        modelProfileForModel(defaults.modelCatalog, stage.model) ||
        fallback.modelProfileId;

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
    const source = textOr(rawRef.source, fallback.source);
    return {
        id: normalizeOptionalEntityId(rawRef.id),
        source,
        uploadFileName: textOr(rawRef.uploadFileName, "") || null,
        uploadedImage: normalizeUploadedMedia(rawRef.uploadedImage),
        frame: Math.max(
            REF_FRAME_MIN,
            Math.round(
                clampedNumber(
                    rawRef.frame,
                    fallback.frame,
                    REF_FRAME_MIN,
                    frameMax,
                ),
            ),
        ),
        fromEnd: !!rawRef.fromEnd,
    };
};
