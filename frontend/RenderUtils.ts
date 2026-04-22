import type { ImageSourceOption } from "./Types";

export const escapeAttr = (value: unknown): string =>
    String(value ?? "")
        .replace(/&/g, "&amp;")
        .replace(/"/g, "&quot;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");

export const renderOptionList = (
    options: ImageSourceOption[],
    selected: string,
): string =>
    options
        .map((option) => {
            const value = escapeAttr(option.value);
            const label = escapeAttr(option.label);
            const isSelected = option.value === selected ? " selected" : "";
            const isDisabled = option.disabled ? " disabled" : "";
            return `<option value="${value}"${isSelected}${isDisabled}>${label}</option>`;
        })
        .join("");

export const clipColorIndex = (clipIndex: number): number =>
    (clipIndex % 4) + 1;

export const framesForClip = (durationSeconds: number, fps: number): number =>
    Math.max(1, Math.round(durationSeconds * Math.max(1, fps)));

/**
 * Round up to a whole frame at the given fps, then truncate to 1 decimal.
 * Mirrors the working.html behavior for stable display values.
 */
export const snapDurationToFps = (seconds: number, fps: number): number => {
    if (
        !Number.isFinite(seconds) ||
        seconds <= 0 ||
        !Number.isFinite(fps) ||
        fps <= 0
    ) {
        return seconds;
    }

    const frames = Math.max(1, Math.ceil(seconds * fps));
    const aligned = frames / fps;
    return Math.max(0.1, Math.floor(aligned * 10) / 10);
};
