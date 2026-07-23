import { isAceStepFunAudioSource } from "./audioSource";
import { AUDIO_SEGMENT_MIN_LENGTH } from "./constants";
import { normalizeUploadedAudio } from "./normalizationMedia";
import {
    clampWindowInDuration,
    normalizeOptionalEntityId,
    readProp,
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
        normalizeOptionalNonNegative(
            readProp(value, "sourceStartSeconds", "SourceStartSeconds"),
        ) ?? 0;
    return {
        id: normalizeOptionalEntityId(readProp(value, "id", "Id")),
        firstClipId: normalizeClipEntityId(
            readProp(value, "firstClipId", "FirstClipId"),
        ),
        lastClipId: normalizeClipEntityId(
            readProp(value, "lastClipId", "LastClipId"),
        ),
        timelineStartSeconds: normalizeOptionalNonNegative(
            readProp(value, "timelineStartSeconds", "TimelineStartSeconds"),
        ),
        timelineLengthSeconds: normalizeOptionalPositive(
            readProp(value, "timelineLengthSeconds", "TimelineLengthSeconds"),
        ),
        sourceStartSeconds: sourceStart,
        clipStartOffsetSeconds: normalizeOptionalNonNegative(
            readProp(value, "clipStartOffsetSeconds", "ClipStartOffsetSeconds"),
        ),
        clipLengthSeconds: normalizeOptionalPositive(
            readProp(value, "clipLengthSeconds", "ClipLengthSeconds"),
        ),
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
        const rawSource = readProp(rawTrack, "source", "Source");
        const source = isRecord(rawSource) ? rawSource : {};
        const rawSpans = readProp(rawTrack, "spans", "Spans");
        tracks.push({
            id: normalizeOptionalEntityId(
                readProp(rawTrack, "id", "Id", "trackId", "TrackId"),
            ),
            source: {
                kind: normalizeAudioTrackSourceKind(
                    readProp(source, "kind", "Kind"),
                ),
                reference:
                    `${readProp(source, "reference", "Reference") ?? ""}`.trim(),
                uploadedAudio: normalizeUploadedAudio(
                    readProp(
                        source,
                        "uploadedAudio",
                        "UploadedAudio",
                        "upload",
                        "Upload",
                    ),
                ),
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
    const rawSource = readProp(value, "source", "Source");
    const source =
        typeof rawSource === "string" && isAceStepFunAudioSource(rawSource)
            ? rawSource.trim()
            : normalizeUploadedAudio(rawSource);
    const startRaw = Math.max(
        0,
        toNumber(`${readProp(value, "startSeconds", "StartSeconds") ?? 0}`, 0),
    );
    const trimStartRaw = Math.max(
        0,
        toNumber(
            `${readProp(value, "trimStartSeconds", "TrimStartSeconds") ?? 0}`,
            0,
        ),
    );
    const lengthRaw = toNumber(
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
        id: normalizeOptionalEntityId(readProp(value, "id", "Id")),
        source,
        startSeconds: roundToTenth(window.startSeconds),
        trimStartSeconds: roundToTenth(trimStartRaw),
        lengthSeconds: roundToTenth(window.lengthSeconds),
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
