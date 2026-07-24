import {
    AUDIO_SEGMENT_VOLUME_DEFAULT,
    AUDIO_SEGMENT_VOLUME_MAX,
    AUDIO_SEGMENT_VOLUME_MIN,
} from "./constants";
import { normalizeUploadedMedia } from "./normalizationMedia";
import {
    clampedNumber,
    normalizeOptionalEntityId,
    optionalNonNegativeNumber,
    optionalPositiveNumber,
    trimmedText,
} from "./normalizationShared";
import type { AudioTrack, AudioTrackSourceKind, AudioTrackSpan } from "./types";
import { isRecord } from "./utils";

const normalizeAudioTrackSourceKind = (
    value: unknown,
): AudioTrackSourceKind => {
    const compact = trimmedText(value).toLowerCase();
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
        optionalNonNegativeNumber(value.sourceStartSeconds) ?? 0;
    return {
        id: normalizeOptionalEntityId(value.id),
        timelineStartSeconds: optionalNonNegativeNumber(
            value.timelineStartSeconds,
        ),
        timelineLengthSeconds: optionalPositiveNumber(
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
                : clampedNumber(
                      rawTrack.volume,
                      AUDIO_SEGMENT_VOLUME_DEFAULT,
                      AUDIO_SEGMENT_VOLUME_MIN,
                      AUDIO_SEGMENT_VOLUME_MAX,
                  );
        tracks.push({
            id: normalizeOptionalEntityId(rawTrack.id),
            source: {
                kind: normalizeAudioTrackSourceKind(source.kind),
                reference: trimmedText(source.reference),
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
