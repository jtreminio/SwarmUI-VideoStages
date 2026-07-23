import {
    AUDIO_SOURCE_NATIVE,
    buildAudioSourceOptions,
    canUseClipLengthFromAudio,
    resolveAudioSourceValue,
} from "./audioSource";
import {
    CONTINUE_OVERLAP_MAX_FRAMES,
    DEFAULT_CONTINUE_OVERLAP_FRAMES,
} from "./boundaryPlan";
import { normalizeStoredHue, UNASSIGNED_HUE } from "./clipColor";
import {
    CLIP_DURATION_MIN,
    DEFAULT_CLIP_DURATION_SECONDS,
    IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH,
    STAGE_REF_STRENGTH_DEFAULT,
} from "./constants";
import { normalizeAudioSegments } from "./normalizationAudio";
import { hasSlotSourcedIcLora, normalizeIcLoras } from "./normalizationIcLora";
import {
    normalizePromptWindows,
    normalizeRetake,
    normalizeSourceVideo,
    normalizeUploadedAudio,
} from "./normalizationMedia";
import { normalizeOptionalEntityId, readProp } from "./normalizationShared";
import {
    buildDefaultRef,
    buildDefaultStage,
    buildDefaultStageRefStrengths,
    getReferenceFrameMax,
    normalizeRef,
    normalizeStage,
} from "./normalizationStage";
import { snapDurationToFps } from "./renderUtils";
import type { BoundaryOut, Clip, RootDefaults, Stage } from "./types";
import { isRecord, toNumber } from "./utils";

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

export const normalizeClip = (
    rawClip: Record<string, unknown>,
    getRootDefaults: () => RootDefaults,
    getDefaultStageModel: (modelValues: string[]) => string,
    effectiveFps?: number,
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
    return {
        id: normalizeOptionalEntityId(readProp(rawClip, "id", "Id")),
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
};
