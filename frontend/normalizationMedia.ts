import {
    clipLengthReferenceIndex,
    clipReferenceCanDriveLength,
    normalizeClipReferenceKind,
    normalizeClipReferenceScale,
} from "./clipReferenceAuthoring";
import {
    CLIP_DURATION_MIN,
    normalizeUploadFileName,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
    RETAKE_STRENGTH_MAX,
    RETAKE_STRENGTH_MIN,
} from "./constants";
import { MEDIA_SOURCE_UPLOAD } from "./generatedMediaSource";
import { REFERENCE_SCALE_FULL } from "./generatedReferenceScale";
import {
    clampedNumber,
    clampWindowInDuration,
    nonNegativeNumber,
    normalizeOptionalEntityId,
    numberOr,
    text,
    trimmedText,
} from "./normalizationShared";
import type {
    ClipReference,
    InitVideo,
    PromptWindow,
    Retake,
    UploadedMedia,
} from "./types";

import { isRecord, roundToTenth } from "./utils";

const normalizePromptWindow = (
    raw: Record<string, unknown>,
): PromptWindow | null => {
    const duration = numberOr(raw.duration, 0);
    if (!(duration > 0)) {
        return null;
    }
    const start = nonNegativeNumber(raw.start);
    return {
        id: normalizeOptionalEntityId(raw.id),
        prompt: text(raw.prompt),
        start,
        duration,
    };
};

export const normalizePromptWindows = (
    rawClip: Record<string, unknown>,
): PromptWindow[] => {
    const rawList = rawClip.promptWindows;
    if (!Array.isArray(rawList)) {
        return [];
    }
    return rawList
        .map((entry) => normalizePromptWindow(isRecord(entry) ? entry : {}))
        .filter((window): window is PromptWindow => window !== null)
        .sort((a, b) => a.start - b.start);
};

export const normalizeRetake = (
    value: unknown,
    clipDuration: number,
): Retake | null => {
    if (!isRecord(value)) {
        return null;
    }
    const startRaw = nonNegativeNumber(value.startSeconds);
    const lengthRaw = numberOr(value.lengthSeconds, 0);
    const window = clampWindowInDuration(
        startRaw,
        lengthRaw,
        clipDuration,
        RETAKE_MIN_DURATION,
    );
    if (!window) {
        return null;
    }
    const strengthRaw = value.strength;
    const strength =
        strengthRaw == null
            ? RETAKE_STRENGTH_DEFAULT
            : clampedNumber(
                  strengthRaw,
                  RETAKE_STRENGTH_DEFAULT,
                  RETAKE_STRENGTH_MIN,
                  RETAKE_STRENGTH_MAX,
              );
    return {
        id: normalizeOptionalEntityId(value.id),
        startSeconds: roundToTenth(window.startSeconds),
        lengthSeconds: roundToTenth(window.lengthSeconds),
        strength,
    };
};

/**
 * A stored source video needs at least a data blob and a positive used-range
 * length. The range is clamped inside the probed file duration when one is
 * known; unknown metadata (fps/duration 0) is preserved — the backend detects
 * fps at runtime, so a failed probe still produces a usable clip.
 */
export const normalizeInitVideo = (value: unknown): InitVideo | null => {
    if (!isRecord(value)) {
        return null;
    }
    const data = trimmedText(value.data);
    if (!data) {
        return null;
    }
    const durationSeconds = nonNegativeNumber(value.durationSeconds);
    let startSeconds = nonNegativeNumber(value.startSeconds);
    let lengthSeconds = nonNegativeNumber(value.lengthSeconds);
    if (durationSeconds > 0) {
        startSeconds = Math.min(
            startSeconds,
            Math.max(0, durationSeconds - CLIP_DURATION_MIN),
        );
        if (!(lengthSeconds > 0)) {
            lengthSeconds = durationSeconds - startSeconds;
        }
        lengthSeconds = Math.min(lengthSeconds, durationSeconds - startSeconds);
    }
    if (!(lengthSeconds > 0)) {
        return null;
    }
    return {
        data,
        fileName: normalizeUploadFileName(
            value.fileName == null ? null : text(value.fileName),
        ),
        fps: nonNegativeNumber(value.fps),
        durationSeconds: roundToTenth(durationSeconds),
        startSeconds: roundToTenth(startSeconds),
        lengthSeconds: roundToTenth(lengthSeconds),
    };
};

/**
 * Whole-clip references are preserved exactly as authored, including an entry
 * whose media is still missing: architecture diagnostics report an unusable
 * reference instead of the document silently dropping one the prompt names.
 *
 * The one thing enforced here is single ownership of the clip's length, the
 * same mutual exclusion the two clip-level length flags already get: at most
 * one reference may claim it, and a clip-level flag outranks all of them.
 */
export const normalizeClipReferences = (
    value: unknown,
    clipLengthClaimedElsewhere = false,
): ClipReference[] => {
    if (!Array.isArray(value)) {
        return [];
    }
    let lengthClaimed = clipLengthClaimedElsewhere;
    return value.map((entry): ClipReference => {
        const raw = isRecord(entry) ? entry : {};
        const kind = normalizeClipReferenceKind(raw.kind);
        const source = trimmedText(raw.source) || MEDIA_SOURCE_UPLOAD;
        const mediaDurationSeconds = roundToTenth(
            nonNegativeNumber(raw.mediaDurationSeconds),
        );
        const reference: ClipReference = {
            id: normalizeOptionalEntityId(raw.id),
            kind,
            source,
            uploadedMedia: normalizeUploadedMedia(raw.uploadedMedia),
            includeSoundtrack:
                kind === "video" && raw.includeSoundtrack === true,
            mediaDurationSeconds,
            // A claim with no length behind it cannot move the clip, so it is
            // dropped rather than left holding the one slot forever.
            drivesClipLength:
                !lengthClaimed &&
                raw.drivesClipLength === true &&
                clipReferenceCanDriveLength({ kind }) &&
                mediaDurationSeconds > 0,
            mediaScale:
                kind === "video"
                    ? normalizeClipReferenceScale(raw.mediaScale)
                    : REFERENCE_SCALE_FULL,
        };
        lengthClaimed = lengthClaimed || reference.drivesClipLength;
        return reference;
    });
};

export const clipReferenceDurationSeconds = (
    references: readonly ClipReference[],
): number | null => {
    const index = clipLengthReferenceIndex(references);
    return index < 0 ? null : references[index].mediaDurationSeconds;
};

export const normalizeUploadedMedia = (
    value: unknown,
): UploadedMedia | null => {
    if (!isRecord(value)) {
        return null;
    }
    const data = trimmedText(value.data);
    if (!data) {
        return null;
    }
    return {
        data,
        fileName: normalizeUploadFileName(
            value.fileName == null ? null : text(value.fileName),
        ),
    };
};
