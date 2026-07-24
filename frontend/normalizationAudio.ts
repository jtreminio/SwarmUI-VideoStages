import {
    AUDIO_SEGMENT_VOLUME_DEFAULT,
    AUDIO_SEGMENT_VOLUME_MAX,
    AUDIO_SEGMENT_VOLUME_MIN,
    clamp,
} from "./constants";
import { normalizeUploadedMedia } from "./normalizationMedia";
import { normalizeOptionalEntityId } from "./normalizationShared";
import type { AudioTrack, AudioTrackSourceKind, AudioTrackSpan } from "./types";
import { isRecord, toNumber } from "./utils";

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
        timelineStartSeconds: normalizeOptionalNonNegative(
            value.timelineStartSeconds,
        ),
        timelineLengthSeconds: normalizeOptionalPositive(
            value.timelineLengthSeconds,
        ),
        sourceStartSeconds: sourceStart,
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
        const volume =
            rawTrack.volume === undefined
                ? undefined
                : clamp(
                      toNumber(
                          `${rawTrack.volume}`,
                          AUDIO_SEGMENT_VOLUME_DEFAULT,
                      ),
                      AUDIO_SEGMENT_VOLUME_MIN,
                      AUDIO_SEGMENT_VOLUME_MAX,
                  );
        tracks.push({
            id: normalizeOptionalEntityId(rawTrack.id),
            source: {
                kind: normalizeAudioTrackSourceKind(source.kind),
                reference: `${source.reference ?? ""}`.trim(),
                uploadedAudio: normalizeUploadedMedia(source.uploadedAudio),
            },
            spans: Array.isArray(rawSpans)
                ? rawSpans
                      .map(normalizeAudioTrackSpan)
                      .filter((span): span is AudioTrackSpan => span !== null)
                : [],
            ...(volume === undefined ? {} : { volume }),
        });
    }
    return tracks;
};
