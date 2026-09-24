import { normalizeArchitectureIcLoras } from "./architectures/behaviorRegistry";
import {
    type BoundaryWindowConstraints,
    boundaryWindowConstraints,
    normalizeBoundaryWindow,
} from "./architectures/boundaryConstraints";
import {
    architectureForModel,
    modelProfileForModel,
} from "./architectures/catalog";
import { architectureDescriptor } from "./architectures/catalogQueries";
import { normalizeClipArchitecture } from "./architectures/identity";
import { NONE_ARCHITECTURE_ID } from "./architectures/none/identity";
import {
    AUDIO_SOURCE_NATIVE,
    AUDIO_SOURCE_PREVIOUS_CLIP,
    canonicalAudioSource,
} from "./audioSource";
import { normalizeStoredHue, UNASSIGNED_HUE } from "./clipColor";
import { normalizeClipReferenceScale } from "./clipReferenceAuthoring";
import { sealSkipSuffix } from "./clipSemantics";
import {
    CLIP_DURATION_MIN,
    DEFAULT_CLIP_DURATION_SECONDS,
    IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH,
    STAGE_REF_STRENGTH_DEFAULT,
} from "./constants";
import { H3_REFERENCE_IMAGE_SIZE_DEFAULT } from "./generatedMiniMaxReferenceImageSize";
import { REFERENCE_SCALE_FULL } from "./generatedReferenceScale";
import { normalizeH3ReferenceImageSize } from "./h3ReferenceImageSize";
import { normalizeH3TextEncoder } from "./h3TextEncoder";
import { defaultLoraWeight } from "./loraAuthoring";
import {
    clipReferenceDurationSeconds,
    normalizeClipReferences,
    normalizeInitVideo,
    normalizePromptWindows,
    normalizeRetake,
    normalizeTimedMediaRange,
    normalizeUploadedMedia,
} from "./normalizationMedia";
import {
    nonNegativeNumber,
    normalizeOptionalEntityId,
    numberOr,
    text,
    trimmedText,
} from "./normalizationShared";
import {
    buildDefaultRef,
    buildDefaultStage,
    buildDefaultStageRefStrengths,
    getKnownReferenceFrameMax,
    normalizeRef,
    normalizeStage,
    normalizeStageIcLoraStrengths,
} from "./normalizationStage";
import { snapDurationToFps } from "./renderUtils";
import type {
    BoundaryOut,
    Clip,
    ReferenceFraming,
    RootDefaults,
    Stage,
} from "./types";
import { isRecord, roundToTenth } from "./utils";

export const normalizeBoundaryOut = (value: unknown): BoundaryOut => {
    const raw = trimmedText(value).toLowerCase();
    return raw === "continue" || raw === "crossfade" ? raw : "cut";
};

/**
 * Catalog updates must preserve authored overlaps for repair. New selections use the active boundary rule.
 */
export const normalizeContinueOverlap = (
    value: unknown,
    constraints: BoundaryWindowConstraints = boundaryWindowConstraints(null),
): number => {
    const numeric = Math.trunc(Number(value));
    return Number.isFinite(numeric) && numeric > 0
        ? numeric
        : normalizeBoundaryWindow(value, constraints);
};

const normalizeReferenceFraming = (value: unknown): ReferenceFraming =>
    value === "stretch" || value === "fit" || value === "fit-green"
        ? value
        : "crop";

export const buildDefaultClip = (
    defaults: RootDefaults,
    defaultStageModel: string,
    includeDefaultRef = false,
    previousClip: Clip | null = null,
): Clip => {
    const frameRefs = includeDefaultRef ? [buildDefaultRef()] : [];
    const firstStage = {
        ...buildDefaultStage(
            defaults,
            defaultStageModel,
            previousClip?.stages[0] ?? null,
            frameRefs.length,
        ),
        loras:
            previousClip?.stages[0]?.loras.map((entry) => ({ ...entry })) ?? [],
        frameRefStrengths: buildDefaultStageRefStrengths(
            frameRefs.length,
            includeDefaultRef
                ? IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH
                : STAGE_REF_STRENGTH_DEFAULT,
        ),
    };
    const architecture =
        (previousClip?.architectureHint !== NONE_ARCHITECTURE_ID
            ? previousClip?.architectureHint
            : null) ??
        architectureForModel(defaults.modelCatalog, firstStage.model) ??
        "unsupported";
    const continueRule = architectureDescriptor(
        defaults.modelCatalog,
        architecture,
    )?.boundaryRules.continue;
    return {
        architectureHint: architecture,
        modelProfileId:
            (previousClip?.architectureHint !== NONE_ARCHITECTURE_ID
                ? previousClip?.modelProfileId
                : null) ??
            modelProfileForModel(defaults.modelCatalog, firstStage.model) ??
            firstStage.modelProfileId,
        skipped: previousClip?.skipped === true,
        hue: UNASSIGNED_HUE,
        boundaryOut: "cut",
        boundaryOutCarryAudio: false,
        boundaryOutReferenceScale: REFERENCE_SCALE_FULL,
        boundaryOutReferenceIncludeSoundtrack: true,
        boundaryOutOverlap:
            boundaryWindowConstraints(continueRule).defaultFrames,
        duration: previousClip
            ? previousClip.duration
            : snapDurationToFps(
                  Math.max(CLIP_DURATION_MIN, DEFAULT_CLIP_DURATION_SECONDS),
                  defaults.fps,
              ),
        useSeedVr: false,
        audioOnly: false,
        refFraming: "crop",
        h3TextEncoder: previousClip?.h3TextEncoder ?? "default",
        h3ReferenceImageSize: H3_REFERENCE_IMAGE_SIZE_DEFAULT,
        audioSource:
            previousClip?.audioOnly === true
                ? AUDIO_SOURCE_PREVIOUS_CLIP
                : AUDIO_SOURCE_NATIVE,
        icLoras: [],
        saveAudioTrack: false,
        clipLengthFromAudio: false,
        clipLengthFromControlNet: false,
        reuseAudio: false,
        uploadedAudio: null,
        uploadedAudioDurationSeconds: 0,
        uploadedAudioStartSeconds: 0,
        uploadedAudioLengthSeconds: 0,
        prompt: "",
        promptWindows: [],
        retake: null,
        initVideo: null,
        references: [],
        frameRefs,
        stages: [firstStage],
    };
};

export const applyAudioOnlyLeadBase = (clips: Clip[]): void => {
    if (clips[0]?.audioOnly === true && clips[1]) {
        clips[1].audioSource = AUDIO_SOURCE_PREVIOUS_CLIP;
    }
};

export const normalizeClip = (
    rawClip: Record<string, unknown>,
    defaults: RootDefaults,
    defaultStageModel: string,
    effectiveFps?: number,
): Clip => {
    const rawAudioSource = text(rawClip.audioSource, AUDIO_SOURCE_NATIVE);
    const stagesRaw = Array.isArray(rawClip.stages) ? rawClip.stages : [];
    const initVideo = normalizeInitVideo(rawClip.initVideo);
    const fps = Math.max(
        1,
        typeof effectiveFps === "number" &&
            Number.isFinite(effectiveFps) &&
            effectiveFps > 0
            ? effectiveFps
            : defaults.fps,
    );
    // Keep unsupported length sources for repair. ControlNet takes precedence over audio, as in the backend.
    const clipLengthFromControlNet = !!rawClip.clipLengthFromControlNet;
    const clipLengthFromAudio =
        !clipLengthFromControlNet && !!rawClip.clipLengthFromAudio;
    const references = normalizeClipReferences(
        rawClip.references,
        clipLengthFromControlNet || clipLengthFromAudio,
    );
    const rawDuration =
        initVideo?.lengthSeconds ??
        clipReferenceDurationSeconds(references) ??
        numberOr(rawClip.duration, defaults.frames / fps);
    const duration = snapDurationToFps(
        Math.max(CLIP_DURATION_MIN, rawDuration),
        fps,
    );
    const refsRaw = Array.isArray(rawClip.keyframes) ? rawClip.keyframes : [];
    const stages: Stage[] = [];
    for (let i = 0; i < stagesRaw.length; i++) {
        const previousStage = i > 0 ? stages[i - 1] : null;
        stages.push(
            normalizeStage(
                defaults,
                defaultStageModel,
                {
                    ...(isRecord(stagesRaw[i]) ? stagesRaw[i] : {}),
                    loras:
                        isRecord(stagesRaw[i]) &&
                        Array.isArray(stagesRaw[i].loras)
                            ? stagesRaw[i].loras
                            : rawClip.loras,
                },
                previousStage,
                refsRaw.length,
                i,
                initVideo !== null,
            ),
        );
    }
    sealSkipSuffix(stages);
    const retake = normalizeRetake(rawClip.retake, duration);
    const audioSource = canonicalAudioSource(rawAudioSource);
    const refFrameMax = getKnownReferenceFrameMax(
        defaults,
        {
            duration,
            stages,
            initVideo,
            retake,
            audioSource,
            clipLengthFromAudio,
            clipLengthFromControlNet,
        },
        fps,
    );
    const frameRefs = refsRaw.map((rawRef) =>
        normalizeRef(isRecord(rawRef) ? rawRef : {}, refFrameMax),
    );
    const stageZero = stages[0] ?? null;
    const persistedArchitecture = trimmedText(rawClip.architectureHint);
    const persistedProfile = trimmedText(rawClip.modelProfileId);
    const isSourceOnly =
        initVideo !== null && stages.every((stage) => stage.skipped);
    const architecture = isSourceOnly
        ? persistedArchitecture || "none"
        : normalizeClipArchitecture(
              persistedArchitecture,
              stageZero?.model ?? null,
              defaults.modelCatalog,
          );
    const resolvedArchitecture = isSourceOnly
        ? NONE_ARCHITECTURE_ID
        : (architectureForModel(
              defaults.modelCatalog,
              stageZero?.model ?? "",
          ) ?? "unsupported");
    const modelProfileId = isSourceOnly
        ? persistedProfile ||
          (architecture === NONE_ARCHITECTURE_ID
              ? NONE_ARCHITECTURE_ID
              : "unsupported")
        : modelProfileForModel(defaults.modelCatalog, stageZero?.model ?? "") ||
          persistedProfile ||
          stageZero?.modelProfileId ||
          "unsupported";
    const icLoras = normalizeArchitectureIcLoras(
        resolvedArchitecture,
        rawClip,
        stagesRaw.length,
        initVideo !== null,
        { preserveDormantLtx: true },
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
    const boundaryOut = normalizeBoundaryOut(rawClip.boundaryOut);
    const boundaryRule = architectureDescriptor(
        defaults.modelCatalog,
        resolvedArchitecture,
    )?.boundaryRules[boundaryOut];
    const uploadedAudio = normalizeUploadedMedia(rawClip.uploadedAudio);
    const uploadedAudioDurationSeconds = uploadedAudio
        ? roundToTenth(nonNegativeNumber(rawClip.uploadedAudioDurationSeconds))
        : 0;
    const uploadedAudioRange = uploadedAudio
        ? normalizeTimedMediaRange(
              rawClip.uploadedAudioStartSeconds,
              rawClip.uploadedAudioLengthSeconds,
              uploadedAudioDurationSeconds,
          )
        : { startSeconds: 0, lengthSeconds: 0 };
    return {
        id: normalizeOptionalEntityId(rawClip.id),
        architectureHint: architecture,
        modelProfileId,
        skipped: !!rawClip.skipped,
        hue: normalizeStoredHue(rawClip.hue),
        boundaryOut,
        boundaryOutCarryAudio: !!rawClip.boundaryOutCarryAudio,
        boundaryOutReferenceScale: normalizeClipReferenceScale(
            rawClip.boundaryOutReferenceScale,
        ),
        boundaryOutReferenceIncludeSoundtrack:
            rawClip.boundaryOutReferenceIncludeSoundtrack !== false,
        boundaryOutOverlap: normalizeContinueOverlap(
            rawClip.boundaryOutOverlap,
            boundaryWindowConstraints(boundaryRule),
        ),
        duration,
        useSeedVr: !!rawClip.useSeedVr,
        audioOnly: !!rawClip.audioOnly,
        refFraming: normalizeReferenceFraming(rawClip.refFraming),
        h3TextEncoder: normalizeH3TextEncoder(rawClip.h3TextEncoder),
        h3ReferenceImageSize: normalizeH3ReferenceImageSize(
            rawClip.h3ReferenceImageSize,
        ),
        audioSource,
        icLoras,
        saveAudioTrack: !!rawClip.saveAudioTrack,
        clipLengthFromAudio,
        clipLengthFromControlNet,
        reuseAudio: !!rawClip.reuseAudio,
        uploadedAudio,
        uploadedAudioDurationSeconds,
        uploadedAudioStartSeconds: uploadedAudioRange.startSeconds,
        uploadedAudioLengthSeconds: uploadedAudioRange.lengthSeconds,
        prompt: text(rawClip.prompt),
        promptWindows: normalizePromptWindows(rawClip),
        retake,
        initVideo,
        references,
        frameRefs,
        stages,
    };
};
