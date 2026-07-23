import { clamp } from "../constants";
import { escapeHtml } from "../timelineDetail";

export const clipInnerWidth = (widthPx: number): number =>
    Math.max(1, widthPx - 2);

export const backgroundImageDataAttr = (source: string): string =>
    ` data-vst-background-image="${escapeHtml(source)}"`;

export const applyBackgroundImages = (root: HTMLElement): void => {
    for (const element of root.querySelectorAll<HTMLElement>(
        "[data-vst-background-image]",
    )) {
        const source = element.dataset.vstBackgroundImage;
        if (source) {
            // CSSOM assignment prevents a media path from escaping a quoted
            // inline style while retaining the exact rendered background.
            element.style.backgroundImage = `url(${JSON.stringify(source)})`;
        }
        element.removeAttribute("data-vst-background-image");
    }
};

export const renderWindowSpan = (options: {
    className: string;
    dataAttrs: string;
    edgeAttr: string;
    labelClass: string;
    label: string;
    title: string;
    ariaLabel: string;
    startSeconds: number;
    lengthSeconds: number;
    durationSeconds: number;
}): string => {
    const start = clamp(options.startSeconds, 0, options.durationSeconds);
    const end = clamp(
        options.startSeconds + options.lengthSeconds,
        start,
        options.durationSeconds,
    );
    if (end <= start) {
        return "";
    }
    const left = (start / options.durationSeconds) * 100;
    const width = ((end - start) / options.durationSeconds) * 100;
    return (
        `<div class="${options.className}" ${options.dataAttrs} style="left:${left}%;width:${width}%" role="button" tabindex="0" title="${escapeHtml(options.title)}" aria-label="${escapeHtml(options.ariaLabel)}">` +
        `<span class="${options.className}-resize ${options.className}-resize-l" ${options.edgeAttr}="left" aria-hidden="true"></span>` +
        `<span class="${options.labelClass}">${escapeHtml(options.label)}</span>` +
        `<span class="${options.className}-resize ${options.className}-resize-r" ${options.edgeAttr}="right" aria-hidden="true"></span>` +
        `</div>`
    );
};

export const headTag = (
    kind: string,
    label: string,
    options?: { active?: boolean; muted?: boolean; style?: string },
): string => {
    const classes =
        `vst-head-tag vst-head-tag-${kind}` +
        (options?.active ? " vst-head-tag-active" : "") +
        (options?.muted ? " vst-head-tag-muted" : "");
    const style = options?.style ? ` style="${options.style}"` : "";
    return (
        `<div class="${classes}"${style} aria-hidden="true">` +
        `<span class="vst-head-tag-pill">${label}</span>` +
        `<span class="vst-head-tag-tick"></span>` +
        `</div>`
    );
};

export const renderTrackHead = (
    iconClass: string,
    icon: string,
    title: string,
    tags: string,
): string =>
    `<div class="vst-track-head">` +
    `<div class="vst-head-top">` +
    `<div class="vst-track-icon ${iconClass}" aria-hidden="true">${icon}</div>` +
    `<div class="vst-track-label"><strong>${title}</strong></div>` +
    `</div>` +
    `<div class="vst-head-tags">${tags}</div>` +
    `</div>`;
