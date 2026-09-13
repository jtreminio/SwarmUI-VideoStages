import { modelProfileForModel } from "./architectures/catalog";
import {
    resolveClipFrameGrid,
    resolvedClipFrameGrid,
} from "./architectures/temporalGrid";
import {
    clamp,
    REF_FRAME_MIN,
    STAGE_REF_STRENGTH_DEFAULT,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "./constants";
import {
    CHARACTER_REFINE_CROP_DIMENSION_MAX,
    CHARACTER_REFINE_CROP_DIMENSION_MIN,
    CHARACTER_REFINE_CROP_DIMENSION_STEP,
    CHARACTER_REFINE_CROP_HEIGHT_DEFAULT,
    CHARACTER_REFINE_CROP_WIDTH_DEFAULT,
    CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
    CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
    CHARACTER_REFINE_FINDER_DEFAULT,
    CHARACTER_REFINE_PART_CONFIDENCE_DEFAULT,
    CHARACTER_REFINE_PART_DEFAULT,
    CHARACTER_REFINE_SUBJECT_CONFIDENCE_DEFAULT,
    CHARACTER_REFINE_SUBJECT_DEFAULT,
} from "./generatedCharacterRefine";
import { MEDIA_SOURCE_REFINER } from "./generatedMediaSource";
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
    snapToStep,
    snapValueToStep,
    textOr,
    trimmedText,
} from "./normalizationShared";
import { framesForClip, NEUTRAL_FRAME_GRID } from "./renderUtils";
import type {
    CharacterRefine,
    Clip,
    FrameRefImage,
    RootDefaults,
    Stage,
} from "./types";
import { isRecord } from "./utils";

const resolveRootPreferredUpscaleMethod = (
    upscaleMethodValues: string[],
): string =>
    upscaleMethodValues.find((value) => value === "pixel-lanczos") ??
    upscaleMethodValues[0] ??
    "";

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
    fallbackStrength = 1,
    perLoraFallbacks: readonly number[] = [],
): number[] => {
    const rawValues = Array.isArray(rawStrengths) ? rawStrengths : [];
    return Array.from({ length: icLoraCount }, (_, index) =>
        normalizeStageControlNetStrengthValue(
            rawValues[index] ?? perLoraFallbacks[index] ?? fallbackStrength,
        ),
    );
};

export const buildDefaultStageRefStrengths = (
    refCount: number,
    defaultStrength = STAGE_REF_STRENGTH_DEFAULT,
): number[] => Array.from({ length: refCount }, () => defaultStrength);

const normalizeStageRefStrengths = (
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

export const normalizeStageLoras = (raw: unknown): Stage["loras"] => {
    if (!Array.isArray(raw)) {
        return [];
    }
    const out: Stage["loras"] = [];
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

export const buildDefaultStage = (
    defaults: RootDefaults,
    defaultStageModel: string,
    previousStage: Stage | null,
    refCount: number,
    initialIcLoraStrengths: readonly number[] = [],
): Stage => {
    const model = previousStage ? previousStage.model : defaultStageModel;
    return {
        loras: [],
        skipped: false,
        control: previousStage ? previousStage.control : defaults.control,
        controlNetStrength: previousStage
            ? previousStage.controlNetStrength
            : STAGE_CONTROLNET_STRENGTH_DEFAULT,
        icLoraStrengths: previousStage
            ? [...previousStage.icLoraStrengths]
            : initialIcLoraStrengths.map(normalizeStageControlNetStrengthValue),
        frameRefStrengths: buildDefaultStageRefStrengths(refCount),
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
        characterRefine: null,
    };
};

export const defaultCharacterRefine = (
    pictureReferenceIds: readonly string[],
): CharacterRefine => ({
    pictureReferenceIds: [...pictureReferenceIds],
    verifyIdentity: pictureReferenceIds.length > 0,
    finder: CHARACTER_REFINE_FINDER_DEFAULT,
    subject: CHARACTER_REFINE_SUBJECT_DEFAULT,
    part: CHARACTER_REFINE_PART_DEFAULT,
    subjectConfidence: CHARACTER_REFINE_SUBJECT_CONFIDENCE_DEFAULT,
    partConfidence: CHARACTER_REFINE_PART_CONFIDENCE_DEFAULT,
    cropWidth: CHARACTER_REFINE_CROP_WIDTH_DEFAULT,
    cropHeight: CHARACTER_REFINE_CROP_HEIGHT_DEFAULT,
});

const referenceIds = (value: unknown): string[] =>
    Array.isArray(value)
        ? [...new Set(value.map(trimmedText).filter(Boolean))]
        : [];

const normalizeCharacterRefine = (value: unknown): CharacterRefine | null => {
    if (!isRecord(value)) return null;
    const legacyIdentity = trimmedText(value.identityReferenceId);
    const pictures = Array.isArray(value.pictureReferenceIds)
        ? referenceIds(value.pictureReferenceIds)
        : referenceIds([legacyIdentity, ...referenceIds(value.referenceIds)]);
    return {
        pictureReferenceIds: pictures,
        verifyIdentity:
            typeof value.verifyIdentity === "boolean"
                ? value.verifyIdentity
                : Boolean(legacyIdentity),
        finder:
            value.finder === "yolo" ? "yolo" : CHARACTER_REFINE_FINDER_DEFAULT,
        subject:
            trimmedText(value.subject) ||
            trimmedText(value.coarseTarget) ||
            CHARACTER_REFINE_SUBJECT_DEFAULT,
        part:
            trimmedText(value.part) ||
            trimmedText(value.fineTarget) ||
            CHARACTER_REFINE_PART_DEFAULT,
        subjectConfidence: clampedNumber(
            value.subjectConfidence ?? value.trackDetectionThreshold,
            CHARACTER_REFINE_SUBJECT_CONFIDENCE_DEFAULT,
            CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
            CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
        ),
        partConfidence: clampedNumber(
            value.partConfidence ?? value.targetDetectionThreshold,
            CHARACTER_REFINE_PART_CONFIDENCE_DEFAULT,
            CHARACTER_REFINE_DETECTION_THRESHOLD_MIN,
            CHARACTER_REFINE_DETECTION_THRESHOLD_MAX,
        ),
        cropWidth: snapValueToStep(
            value.cropWidth,
            CHARACTER_REFINE_CROP_WIDTH_DEFAULT,
            CHARACTER_REFINE_CROP_DIMENSION_MIN,
            CHARACTER_REFINE_CROP_DIMENSION_MAX,
            CHARACTER_REFINE_CROP_DIMENSION_STEP,
        ),
        cropHeight: snapValueToStep(
            value.cropHeight,
            CHARACTER_REFINE_CROP_HEIGHT_DEFAULT,
            CHARACTER_REFINE_CROP_DIMENSION_MIN,
            CHARACTER_REFINE_CROP_DIMENSION_MAX,
            CHARACTER_REFINE_CROP_DIMENSION_STEP,
        ),
    };
};

export const buildDefaultRef = (
    source: string = MEDIA_SOURCE_REFINER,
): FrameRefImage => ({
    source,
    uploadFileName: null,
    uploadedImage: null,
    frame: REF_FRAME_MIN,
    fromEnd: false,
});

export const appendRefToClip = (clip: Clip, ref: FrameRefImage): void => {
    clip.frameRefs.push(ref);
    for (const stage of clip.stages) {
        stage.frameRefStrengths.push(STAGE_REF_STRENGTH_DEFAULT);
    }
};

export const removeRefAt = (clip: Clip, refIdx: number): boolean => {
    if (refIdx < 0 || refIdx >= clip.frameRefs.length) {
        return false;
    }
    clip.frameRefs.splice(refIdx, 1);
    for (const stage of clip.stages) {
        if (refIdx < stage.frameRefStrengths.length) {
            stage.frameRefStrengths.splice(refIdx, 1);
        }
    }
    return true;
};

export const appendIcLoraStrengthToClip = (
    clip: Clip,
    initialStrength = 1,
): void => {
    for (const stage of clip.stages) {
        stage.icLoraStrengths.push(
            normalizeStageControlNetStrengthValue(initialStrength),
        );
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
    defaults: RootDefaults,
    clip?: Pick<Clip, "duration"> &
        Partial<
            Pick<
                Clip,
                | "stages"
                | "initVideo"
                | "retake"
                | "audioSource"
                | "clipLengthFromAudio"
                | "clipLengthFromControlNet"
            >
        >,
    effectiveFps?: number,
): number => {
    const fps =
        typeof effectiveFps === "number" &&
        Number.isFinite(effectiveFps) &&
        effectiveFps > 0
            ? effectiveFps
            : defaults.fps;
    if (clip) {
        const frameGrid = clip.stages
            ? resolvedClipFrameGrid(
                  { ...clip, stages: clip.stages },
                  defaults.modelCatalog,
              )
            : NEUTRAL_FRAME_GRID;
        return Math.max(
            REF_FRAME_MIN,
            framesForClip(clip.duration, fps, frameGrid),
        );
    }
    return Math.max(REF_FRAME_MIN, defaults.frames);
};

/**
 * Upper bound suitable for destructive normalization. Unknown, conflicting, and dormant model
 * facts have no safe upper bound: preserve their authored reference positions for later repair.
 */
export const getKnownReferenceFrameMax = (
    defaults: RootDefaults,
    clip: Pick<Clip, "duration" | "stages"> &
        Partial<
            Pick<
                Clip,
                | "initVideo"
                | "retake"
                | "audioSource"
                | "clipLengthFromAudio"
                | "clipLengthFromControlNet"
            >
        >,
    effectiveFps?: number,
): number | null => {
    const resolution = resolveClipFrameGrid(clip, defaults.modelCatalog);
    if (resolution.status !== "resolved") {
        return null;
    }
    const fps =
        typeof effectiveFps === "number" &&
        Number.isFinite(effectiveFps) &&
        effectiveFps > 0
            ? effectiveFps
            : defaults.fps;
    return Math.max(
        REF_FRAME_MIN,
        framesForClip(clip.duration, fps, {
            frameGrid: resolution.frameGrid,
            frameGridOrigin: resolution.frameGridOrigin,
        }),
    );
};

export const normalizeStage = (
    defaults: RootDefaults,
    defaultStageModel: string,
    rawStage: Record<string, unknown>,
    previousStage: Stage | null,
    refCount: number,
    stageIndexInClip: number,
    initVideoClip = false,
): Stage => {
    const fallback = buildDefaultStage(
        defaults,
        defaultStageModel,
        previousStage,
        refCount,
    );
    // Generated stage 0 forces control/upscale. Init-video stage 0 refines footage, so authored
    // values remain active.
    const forcedFirstStage = stageIndexInClip === 0 && !initVideoClip;
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
        loras: normalizeStageLoras(rawStage.loras),
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
        frameRefStrengths: normalizeStageRefStrengths(
            rawStage.keyframeStrengths,
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
        characterRefine: normalizeCharacterRefine(rawStage.characterRefine),
    };
    if (stage.characterRefine) {
        stage.upscale = 1;
    }
    // The catalog owns resolved models; a persisted hint only repairs an unresolved model.
    stage.modelProfileId =
        modelProfileForModel(defaults.modelCatalog, stage.model) ||
        trimmedText(readRawStageProp(rawStage, "modelProfileId")) ||
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
    frameMax: number | null,
): FrameRefImage => {
    const fallback = buildDefaultRef();
    const source = textOr(rawRef.source, fallback.source);
    const authoredFrame = Math.max(
        REF_FRAME_MIN,
        Math.round(numberOr(rawRef.frame, fallback.frame)),
    );
    return {
        id: normalizeOptionalEntityId(rawRef.id),
        source,
        uploadFileName: textOr(rawRef.uploadFileName, "") || null,
        uploadedImage: normalizeUploadedMedia(rawRef.uploadedImage),
        frame:
            frameMax === null
                ? authoredFrame
                : clamp(authoredFrame, REF_FRAME_MIN, frameMax),
        fromEnd: !!rawRef.fromEnd,
    };
};
