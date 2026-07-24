import {
    hasArchitectureSlotSourcedIcLora,
    normalizeArchitectureIcLoras,
} from "./architectures/behaviorRegistry";
import {
    type BoundaryOverlapConstraints,
    boundaryOverlapConstraints,
    normalizeBoundaryOverlap,
} from "./architectures/boundaryConstraints";
import {
    architectureForModel,
    modelProfileForModel,
} from "./architectures/catalog";
import { normalizeClipArchitecture } from "./architectures/identity";
import { AUDIO_SOURCE_NATIVE, canUseClipLengthFromAudio } from "./audioSource";
import { normalizeStoredHue, UNASSIGNED_HUE } from "./clipColor";
import {
    CLIP_DURATION_MIN,
    DEFAULT_CLIP_DURATION_SECONDS,
    IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH,
    STAGE_REF_STRENGTH_DEFAULT,
} from "./constants";
import {
    normalizePromptWindows,
    normalizeRetake,
    normalizeSourceVideo,
    normalizeUploadedMedia,
} from "./normalizationMedia";
import { normalizeOptionalEntityId } from "./normalizationShared";
import {
    buildDefaultRef,
    buildDefaultStage,
    buildDefaultStageRefStrengths,
    getReferenceFrameMax,
    normalizeRef,
    normalizeStage,
    normalizeStageIcLoraStrengths,
} from "./normalizationStage";
import { snapDurationToFps } from "./renderUtils";
import type { BoundaryOut, Clip, RootDefaults, Stage } from "./types";
import { isRecord, toNumber } from "./utils";

export const normalizeBoundaryOut = (value: unknown): BoundaryOut => {
    const raw = `${value ?? ""}`.trim().toLowerCase();
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
    const firstStage = {
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
    };
    const architecture =
        (previousClip?.architecture !== "none"
            ? previousClip?.architecture
            : null) ??
        architectureForModel(defaults.modelCatalog, firstStage.model) ??
        "unsupported";
    const continueRule = defaults.modelCatalog.architectures.find(
        (entry) => entry.id === architecture,
    )?.boundaryRules.continue;
    return {
        architecture,
        modelProfileId:
            (previousClip?.architecture !== "none"
                ? previousClip?.modelProfileId
                : null) ??
            modelProfileForModel(defaults.modelCatalog, firstStage.model) ??
            firstStage.modelProfileId,
        skipped: false,
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
    const rawAudioSource = `${rawClip.audioSource ?? AUDIO_SOURCE_NATIVE}`;
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
        toNumber(`${rawClip.duration}`, defaults.frames / fps);
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
    const audioSource = rawAudioSource.trim() || AUDIO_SOURCE_NATIVE;
    const stageZero = stages[0] ?? null;
    const persistedArchitecture = `${rawClip.architecture ?? ""}`.trim();
    const persistedProfile = `${rawClip.modelProfileId ?? ""}`.trim();
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
        ? persistedProfile || (architecture === "none" ? "none" : "unsupported")
        : persistedProfile || stageZero?.modelProfileId || "unsupported";
    const icLoras = normalizeArchitectureIcLoras(
        architecture,
        rawClip,
        stagesRaw.length,
        sourceVideo !== null,
        !defaults.modelCatalog.architectures.some(
            (entry) => entry.id === architecture,
        ) || architecture === "none",
    );
    for (const stage of stages) {
        stage.icLoraStrengths = normalizeStageIcLoraStrengths(
            stage.icLoraStrengths,
            icLoras.length,
            stage.controlNetStrength,
        );
    }
    const clipLengthFromAudio =
        canUseClipLengthFromAudio(audioSource) && !!rawClip.clipLengthFromAudio;
    const clipLengthFromControlNet =
        hasArchitectureSlotSourcedIcLora(architecture, icLoras) &&
        !clipLengthFromAudio &&
        !!rawClip.clipLengthFromControlNet;
    const boundaryOut = normalizeBoundaryOut(rawClip.boundaryOut);
    const boundaryRule = defaults.modelCatalog.architectures.find(
        (entry) => entry.id === architecture,
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
        icLoras,
        saveAudioTrack: !!rawClip.saveAudioTrack,
        clipLengthFromAudio,
        clipLengthFromControlNet,
        reuseAudio: !!rawClip.reuseAudio,
        uploadedAudio: normalizeUploadedMedia(rawClip.uploadedAudio),
        prompt: `${rawClip.prompt ?? ""}`,
        promptWindows: normalizePromptWindows(rawClip),
        retake: normalizeRetake(rawClip.retake, duration),
        sourceVideo,
        refs,
        stages,
    };
};
