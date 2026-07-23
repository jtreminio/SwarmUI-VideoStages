import { isAceStepFunAudioSource } from "./audioSource";
import {
    AUDIO_SEGMENT_MIN_LENGTH,
    AUDIO_SEGMENT_VOLUME_DEFAULT,
    AUDIO_SEGMENT_VOLUME_MAX,
    AUDIO_SEGMENT_VOLUME_MIN,
    clamp,
} from "./constants";
import { normalizeUploadedAudio } from "./normalizationMedia";
import {
    clampWindowInDuration,
    normalizeOptionalEntityId,
} from "./normalizationShared";
import type {
    AudioSegment,
    AudioTrack,
    AudioTrackSourceKind,
    AudioTrackSpan,
} from "./types";
import { isRecord, roundToTenth, toNumber } from "./utils";

const normalizeOptionalNonNegative = (value: unknown): number | null => {
    if (value == null || `${value}`.trim() === "") {
        return null;
    }
    const number = toNumber(`${value}`, Number.NaN);
    return Number.isFinite(number) && number >= 0 ? number : null;
};

const normalizeOptionalPositive = (value: unknown): number | null => {
    const number = normalizeOptionalNonNegative(value);
    return number !== null && number > 0 ? number : null;
};

const normalizeAudioTrackSourceKind = (
    value: unknown,
): AudioTrackSourceKind => {
    const compact = `${value ?? ""}`.trim().toLowerCase();
    switch (compact) {
        case "upload":
            return "Upload";
        case "acestepfun":
            return "AceStepFun";
        case "native":
            return "Native";
        case "controlnet":
            return "ControlNet";
        default:
            return "External";
    }
};

const normalizeClipEntityId = (value: unknown): string | null =>
    normalizeOptionalEntityId(value) ?? null;

export const normalizeAudioTrackSpan = (
    value: unknown,
): AudioTrackSpan | null => {
    if (!isRecord(value)) {
        return null;
    }
    const sourceStart =
        normalizeOptionalNonNegative(value.sourceStartSeconds) ?? 0;
    return {
        id: normalizeOptionalEntityId(value.id),
        firstClipId: normalizeClipEntityId(value.firstClipId),
        lastClipId: normalizeClipEntityId(value.lastClipId),
        timelineStartSeconds: normalizeOptionalNonNegative(
            value.timelineStartSeconds,
        ),
        timelineLengthSeconds: normalizeOptionalPositive(
            value.timelineLengthSeconds,
        ),
        sourceStartSeconds: sourceStart,
        clipStartOffsetSeconds: normalizeOptionalNonNegative(
            value.clipStartOffsetSeconds,
        ),
        clipLengthSeconds: normalizeOptionalPositive(value.clipLengthSeconds),
    };
};

export const normalizeAudioTracks = (value: unknown): AudioTrack[] => {
    if (!Array.isArray(value)) {
        return [];
    }
    const tracks: AudioTrack[] = [];
    for (const rawTrack of value) {
        if (!isRecord(rawTrack)) {
            continue;
        }
        const rawSource = rawTrack.source;
        const source = isRecord(rawSource) ? rawSource : {};
        const rawSpans = rawTrack.spans;
        tracks.push({
            id: normalizeOptionalEntityId(rawTrack.id),
            source: {
                kind: normalizeAudioTrackSourceKind(source.kind),
                reference: `${source.reference ?? ""}`.trim(),
                uploadedAudio: normalizeUploadedAudio(source.uploadedAudio),
            },
            spans: Array.isArray(rawSpans)
                ? rawSpans
                      .map(normalizeAudioTrackSpan)
                      .filter((span): span is AudioTrackSpan => span !== null)
                : [],
        });
    }
    return tracks;
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
    const rawSource = value.source;
    const source =
        typeof rawSource === "string" && isAceStepFunAudioSource(rawSource)
            ? rawSource.trim()
            : normalizeUploadedAudio(rawSource);
    const startRaw = Math.max(0, toNumber(`${value.startSeconds ?? 0}`, 0));
    const trimStartRaw = Math.max(
        0,
        toNumber(`${value.trimStartSeconds ?? 0}`, 0),
    );
    const lengthRaw = toNumber(`${value.lengthSeconds ?? 0}`, 0);
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
        id: normalizeOptionalEntityId(value.id),
        source,
        startSeconds: roundToTenth(window.startSeconds),
        trimStartSeconds: roundToTenth(trimStartRaw),
        lengthSeconds: roundToTenth(window.lengthSeconds),
        volume: clamp(
            toNumber(
                `${value.volume ?? AUDIO_SEGMENT_VOLUME_DEFAULT}`,
                AUDIO_SEGMENT_VOLUME_DEFAULT,
            ),
            AUDIO_SEGMENT_VOLUME_MIN,
            AUDIO_SEGMENT_VOLUME_MAX,
        ),
    };
};

/**
 * Normalizes the optional per-clip audio segment list against the clip
 * duration. Array ORDER is preserved — the index is the segment's timeline
 * lane, and lanes must not reshuffle as segments move. Returns [] when absent.
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
