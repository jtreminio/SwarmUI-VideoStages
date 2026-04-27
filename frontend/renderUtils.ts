import type { ImageSourceOption } from "./types";

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

const FRAME_ALIGNMENT = 8;

export const framesForClip = (durationSeconds: number, fps: number): number =>
    Math.max(
        1,
        Math.ceil(
            Math.max(0, Math.ceil(durationSeconds * Math.max(1, fps))) /
                FRAME_ALIGNMENT,
        ) *
            FRAME_ALIGNMENT +
            1,
    );

export const clipFieldId = (clipIdx: number, field: string): string =>
    `vsclip${clipIdx}_${field}`;

export const refFieldId = (
    clipIdx: number,
    refIdx: number,
    field: string,
): string => `vsclip${clipIdx}_ref${refIdx}_${field}`;

export const stageFieldId = (
    clipIdx: number,
    stageIdx: number,
    field: string,
): string => `vsclip${clipIdx}_stage${stageIdx}_${field}`;

// Route changes via data-* on controls from make*Input; nogrow skips SwarmUI auto-width.
export const injectFieldData = (
    html: string,
    dataAttrs: Record<string, string>,
): string => {
    const dataAttrString = Object.entries(dataAttrs)
        .map(([key, value]) => `${key}="${escapeAttr(value)}"`)
        .join(" ");
    return html.replace(
        /<(input|select|textarea)\s+class="([^"]*)"/g,
        (_match, tag, classes) =>
            `<${tag} class="${classes} nogrow" ${dataAttrString}`,
    );
};

export const overrideSliderSteps = (
    html: string,
    config: {
        numberStep?: number | "any";
        rangeStep?: number | "any";
    },
): string => {
    let updated = html;
    if (config.numberStep !== undefined) {
        updated = updated.replace(
            /(<input\b[^>]*type="number"[^>]*\sstep=")[^"]*(")/g,
            (_match, prefix, suffix) =>
                `${prefix}${String(config.numberStep)}${suffix}`,
        );
    }
    if (config.rangeStep !== undefined) {
        updated = updated.replace(
            /(<input\b[^>]*type="range"[^>]*\sstep=")[^"]*(")/g,
            (_match, prefix, suffix) =>
                `${prefix}${String(config.rangeStep)}${suffix}`,
        );
    }
    return updated;
};

// Round up to a frame at fps, then one decimal place (matches working.html).
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
