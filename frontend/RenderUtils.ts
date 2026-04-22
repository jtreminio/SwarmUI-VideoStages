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
 * Builds a unique field id for a clip-level field.
 *
 * The id is required by SwarmUI's `make*Input` helpers (they wire labels and
 * popovers to it). We never read this id back from our own code -- our
 * delegated event handler keys off the `data-clip-field`/`data-clip-idx`
 * attributes that {@link injectFieldData} stamps on the inner control.
 */
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

/**
 * Splices our editor-routing data attributes onto the inner control(s)
 * (`<input>`, `<select>`, or `<textarea>`) emitted by SwarmUI's `make*Input`
 * helpers so the existing delegated change handler can route by clip / ref /
 * stage index without us forking the helper output. Slider helpers emit both
 * a number input and a range input, and both need the same routing data.
 *
 * `nogrow` is added because SwarmUI's `autoNumberWidth` / `autoSelectWidth`
 * runnables stamp inline `style.width` from option/value text width. In our
 * grid layout we want the controls to fill their cell instead, and the
 * helpers explicitly bail when they see this class.
 */
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
