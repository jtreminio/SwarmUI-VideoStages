import {
    CLIP_DURATION_MIN,
    clamp,
    normalizeUploadFileName,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
    RETAKE_STRENGTH_MAX,
    RETAKE_STRENGTH_MIN,
} from "./constants";
import {
    clampWindowInDuration,
    normalizeOptionalEntityId,
    readProp,
} from "./normalizationShared";
import type { PromptWindow, Retake, SourceVideo, UploadedAudio } from "./types";
import { isRecord, roundToTenth, toNumber } from "./utils";

const normalizePromptWindow = (
    raw: Record<string, unknown>,
): PromptWindow | null => {
    const duration = toNumber(
        `${readProp(raw, "duration", "Duration") ?? 0}`,
        0,
    );
    if (!(duration > 0)) {
        return null;
    }
    const start = Math.max(
        0,
        toNumber(`${readProp(raw, "start", "Start") ?? 0}`, 0),
    );
    return {
        id: normalizeOptionalEntityId(readProp(raw, "id", "Id")),
        prompt: `${readProp(raw, "prompt", "Prompt", "text", "Text") ?? ""}`,
        start,
        duration,
    };
};

export const normalizePromptWindows = (
    rawClip: Record<string, unknown>,
): PromptWindow[] => {
    const rawList = readProp(rawClip, "promptWindows", "PromptWindows");
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
    const startRaw = Math.max(
        0,
        toNumber(`${readProp(value, "startSeconds", "StartSeconds") ?? 0}`, 0),
    );
    const lengthRaw = toNumber(
        `${readProp(value, "lengthSeconds", "LengthSeconds") ?? 0}`,
        0,
    );
    const window = clampWindowInDuration(
        startRaw,
        lengthRaw,
        clipDuration,
        RETAKE_MIN_DURATION,
    );
    if (!window) {
        return null;
    }
    const strengthRaw = readProp(value, "strength", "Strength");
    const strength =
        strengthRaw == null
            ? RETAKE_STRENGTH_DEFAULT
            : clamp(
                  toNumber(`${strengthRaw}`, RETAKE_STRENGTH_DEFAULT),
                  RETAKE_STRENGTH_MIN,
                  RETAKE_STRENGTH_MAX,
              );
    return {
        id: normalizeOptionalEntityId(readProp(value, "id", "Id")),
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
export const normalizeSourceVideo = (value: unknown): SourceVideo | null => {
    if (!isRecord(value)) {
        return null;
    }
    const data = `${value.data ?? ""}`.trim();
    if (!data) {
        return null;
    }
    const nonNegative = (raw: unknown): number =>
        Math.max(0, toNumber(`${raw ?? 0}`, 0));
    const durationSeconds = nonNegative(
        readProp(value, "durationSeconds", "DurationSeconds"),
    );
    let startSeconds = nonNegative(
        readProp(value, "startSeconds", "StartSeconds"),
    );
    let lengthSeconds = nonNegative(
        readProp(value, "lengthSeconds", "LengthSeconds"),
    );
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
            value.fileName == null ? null : `${value.fileName}`,
        ),
        fps: nonNegative(readProp(value, "fps", "Fps")),
        durationSeconds: roundToTenth(durationSeconds),
        startSeconds: roundToTenth(startSeconds),
        lengthSeconds: roundToTenth(lengthSeconds),
    };
};

export const normalizeUploadedAudio = (
    value: unknown,
): UploadedAudio | null => {
    if (!isRecord(value)) {
        return null;
    }
    const data = `${value.data ?? ""}`.trim();
    if (!data) {
        return null;
    }
    return {
        data,
        fileName: normalizeUploadFileName(
            value.fileName == null ? null : `${value.fileName}`,
        ),
    };
};
