import { normalizeArchitectureIcLoras } from "./architectures/behaviorRegistry";
import {
    type BoundaryOverlapConstraints,
    boundaryOverlapConstraints,
    normalizeBoundaryOverlap,
} from "./architectures/boundaryConstraints";
import {
    architectureForModel,
    modelProfileForModel,
} from "./architectures/catalog";
import { architectureDescriptor } from "./architectures/catalogQueries";
import { normalizeClipArchitecture } from "./architectures/identity";
import { NONE_ARCHITECTURE_ID } from "./architectures/none/definition";
import { AUDIO_SOURCE_NATIVE } from "./audioSource";
import { normalizeStoredHue, UNASSIGNED_HUE } from "./clipColor";
import {
    CLIP_DURATION_MIN,
    DEFAULT_CLIP_DURATION_SECONDS,
    IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH,
    STAGE_REF_STRENGTH_DEFAULT,
} from "./constants";
import { defaultLoraWeight } from "./loraAuthoring";
import {
    normalizePromptWindows,
    normalizeRetake,
    normalizeSourceVideo,
    normalizeUploadedMedia,
} from "./normalizationMedia";
import {
    normalizeOptionalEntityId,
    numberOr,
    text,
    trimmedText,
} from "./normalizationShared";
import {
    buildDefaultRef,
    buildDefaultStage,
    buildDefaultStageRefStrengths,
    getReferenceFrameMax,
    normalizeRef,
    normalizeStage,
    normalizeStageIcLoraStrengths,
    normalizeStageLoras,
} from "./normalizationStage";
import { snapDurationToFps } from "./renderUtils";
import type { BoundaryOut, Clip, RootDefaults, Stage } from "./types";
import { isRecord } from "./utils";

export const normalizeBoundaryOut = (value: unknown): BoundaryOut => {
    const raw = trimmedText(value).toLowerCase();
    return raw === "continue" || raw === "crossfade" ? raw : "cut";
};

/**
 * Preserves positive authored values exactly so a catalog update cannot
 * silently rewrite an overlap before the user repairs it. New UI selections
 * are normalized against the active boundary rule.
 */
export const normalizeContinueOverlap = (
    value: unknown,
    constraints: BoundaryOverlapConstraints = boundaryOverlapConstraints(null),
): number => {
    const numeric = Math.trunc(Number(value));
    return Number.isFinite(numeric) && numeric > 0
        ? numeric
        : normalizeBoundaryOverlap(value, constraints);
};

export const buildDefaultClip = (
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    includeDefaultRef = false,
    previousClip: Clip | null = null,
): Clip => {
    const defaults = getRootDefaults();
    const refs = includeDefaultRef ? [buildDefaultRef()] : [];
    const loras = previousClip?.loras.map((entry) => ({ ...entry })) ?? [];
    const initialLoraWeights = loras.map(
        (entry, index) =>
            previousClip?.stages[0]?.loraWeights[index] ??
            defaults.loraDefaultWeights[
                defaults.loraValues.indexOf(entry.name)
            ] ??
            1,
    );
    const firstStage = {
        ...buildDefaultStage(
            getRootDefaults,
            getDefaultStageModel,
            previousClip?.stages[0] ?? null,
            refs.length,
            initialLoraWeights,
        ),
        refStrengths: buildDefaultStageRefStrengths(
            refs.length,
            includeDefaultRef
                ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH
                : STAGE_REF_STRENGTH_DEFAULT,
        ),
    };
    const architecture =
        (previousClip?.architecture !== NONE_ARCHITECTURE_ID
            ? previousClip?.architecture
            : null) ??
        architectureForModel(defaults.modelCatalog, firstStage.model) ??
        "unsupported";
    const continueRule = architectureDescriptor(
        defaults.modelCatalog,
        architecture,
    )?.boundaryRules.continue;
    return {
        architecture,
        modelProfileId:
            (previousClip?.architecture !== NONE_ARCHITECTURE_ID
                ? previousClip?.modelProfileId
                : null) ??
            modelProfileForModel(defaults.modelCatalog, firstStage.model) ??
            firstStage.modelProfileId,
        skipped: previousClip?.skipped === true,
        hue: UNASSIGNED_HUE,
        boundaryOut: "cut",
        boundaryOutCarryAudio: false,
        boundaryOutOverlap:
            boundaryOverlapConstraints(continueRule).defaultFrames,
        duration: previousClip
            ? previousClip.duration
            : snapDurationToFps(
                  Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
                  defaults.fps,
              ),
        audioSource: AUDIO_SOURCE_NATIVE,
        loras,
        icLoras: [],
        saveAudioTrack: false,
        clipLengthFromAudio: false,
        clipLengthFromControlNet: false,
        reuseAudio: false,
        uploadedAudio: null,
        prompt: "",
        promptWindows: [],
        retake: null,
        sourceVideo: null,
        refs,
        stages: [firstStage],
    };
};

export const normalizeClip = (
    rawClip: Record<string, unknown>,
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    effectiveFps?: number,
): Clip => {
    const defaults = getRootDefaults();
    const rawAudioSource = text(rawClip.audioSource, AUDIO_SOURCE_NATIVE);
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const sourceVideo = normalizeSourceVideo(rawClip.sourceVideo);
    const fps = Math.max(
        1,
        typeof effectiveFps === "number" &&
            Number.isFinite(effectiveFps) &&
            effectiveFps > 0
            ? effectiveFps
            : defaults.fps,
    );
    // A sourced clip's duration IS its used source range.
    const rawDuration =
        sourceVideo?.lengthSeconds ??
        numberOr(rawClip.duration, defaults.frames / fps);
    const duration = snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, rawDuration),
        fps,
    );
    const refsRaw = Array.isArray(rawClip.refs) ? rawClip.refs : [];
    const refFrameMax = getReferenceFrameMax(
        getRootDefaults,
        { duration },
        fps,
    );
    const refs = refsRaw.map((rawRef) =>
        normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax),
    );
    const clipScopedLoras = normalizeStageLoras(rawClip.loras);
    const loraNames: string[] = [];
    const loraDefaultWeightByName = new Map<string, number>();
    const appendLoraName = (name: string, defaultWeight: number): void => {
        if (loraDefaultWeightByName.has(name)) {
            return;
        }
        loraNames.push(name);
        loraDefaultWeightByName.set(name, defaultWeight);
    };
    for (const entry of clipScopedLoras) {
        appendLoraName(entry.name, entry.weight);
    }
    for (const rawStage of stagesRaw) {
        if (!isRecord(rawStage)) {
            continue;
        }
        for (const entry of normalizeStageLoras(rawStage.loras)) {
            // A legacy stage-local LoRA is absent from other stages unless
            // those stages name it too.
            appendLoraName(entry.name, 0);
        }
    }
    const loras = loraNames.map((name) => ({ name }));
    const loraDefaultWeights = loraNames.map(
        (name) => loraDefaultWeightByName.get(name) ?? 1,
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
                loras,
                loraDefaultWeights,
            ),
        );
    }
    const firstSkippedStage = stages.findIndex(
        (stage) => stage.skipped === true,
    );
    if (firstSkippedStage >= 0) {
        for (let index = firstSkippedStage; index < stages.length; index++) {
            stages[index].skipped = true;
        }
    }
    const audioSource = rawAudioSource.trim() || AUDIO_SOURCE_NATIVE;
    const stageZero = stages[0] ?? null;
    const persistedArchitecture = trimmedText(rawClip.architecture);
    const persistedProfile = trimmedText(rawClip.modelProfileId);
    const isSourceOnly =
        sourceVideo !== null && stages.every((stage) => stage.skipped);
    const architecture = isSourceOnly
        ? persistedArchitecture || "none"
        : normalizeClipArchitecture(
              persistedArchitecture,
              stageZero?.model ?? null,
              defaults.modelCatalog,
          );
    const modelProfileId = isSourceOnly
        ? persistedProfile ||
          (architecture === NONE_ARCHITECTURE_ID
              ? NONE_ARCHITECTURE_ID
              : "unsupported")
        : persistedProfile || stageZero?.modelProfileId || "unsupported";
    const icLoras = normalizeArchitectureIcLoras(
        architecture,
        rawClip,
        stagesRaw.length,
        sourceVideo !== null,
        architectureDescriptor(defaults.modelCatalog, architecture) === null ||
            architecture === NONE_ARCHITECTURE_ID,
    );
    const icLoraDefaultStrengths = icLoras.map((entry) =>
        defaultLoraWeight(defaults, entry.lora),
    );
    for (let index = 0; index < stages.length; index++) {
        const stage = stages[index];
        const rawStage = isRecord(stagesRaw[index]) ? stagesRaw[index] : {};
        const hasLegacyControlNetStrength = Object.hasOwn(
            rawStage,
            "controlNetStrength",
        );
        const legacyFallback = hasLegacyControlNetStrength
            ? stage.controlNetStrength
            : 1;
        stage.icLoraStrengths = normalizeStageIcLoraStrengths(
            rawStage.icLoraStrengths,
            icLoras.length,
            legacyFallback,
            hasLegacyControlNetStrength
                ? []
                : (stages[index - 1]?.icLoraStrengths ??
                      icLoraDefaultStrengths),
        );
    }
    // Preserved exactly as authored: a source that cannot supply a length is
    // reported by architecture diagnostics, never silently erased here. Only
    // the mutual exclusion between the two flags is enforced.
    const clipLengthFromAudio = !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet =
        !clipLengthFromAudio && !!rawClip.clipLengthFromControlNet;
    const boundaryOut = normalizeBoundaryOut(rawClip.boundaryOut);
    const boundaryRule = architectureDescriptor(
        defaults.modelCatalog,
        architecture,
    )?.boundaryRules[boundaryOut];
    return {
        id: normalizeOptionalEntityId(rawClip.id),
        architecture,
        modelProfileId,
        skipped: !!rawClip.skipped,
        hue: normalizeStoredHue(rawClip.hue),
        boundaryOut,
        boundaryOutCarryAudio: !!rawClip.boundaryOutCarryAudio,
        boundaryOutOverlap: normalizeContinueOverlap(
            rawClip.boundaryOutOverlap,
            boundaryOverlapConstraints(boundaryRule),
        ),
        duration,
        audioSource,
        loras,
        icLoras,
        saveAudioTrack: !!rawClip.saveAudioTrack,
        clipLengthFromAudio,
        clipLengthFromControlNet,
        reuseAudio: !!rawClip.reuseAudio,
        uploadedAudio: normalizeUploadedMedia(rawClip.uploadedAudio),
        prompt: text(rawClip.prompt),
        promptWindows: normalizePromptWindows(rawClip),
        retake: normalizeRetake(rawClip.retake, duration),
        sourceVideo,
        refs,
        stages,
    };
};
