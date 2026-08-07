import type {
    CapabilityViewResolver,
    GeneratedArchitectureFeature,
} from "../architectures/policy";
import { escapeAttr as escapeHtml } from "../renderUtils";
import { spanGeometry } from "../trackDomUtils";
import type { Clip } from "../types";

/** A clip keeps a mini-lane while it has data to remove, or a precondition (the code) it could still meet. */
export const laneVisible = (
    clip: Clip,
    feature: GeneratedArchitectureFeature,
    persisted: boolean,
    capabilities?: CapabilityViewResolver,
): boolean => {
    const decision = capabilities?.forClip(clip).decision(feature);
    return (
        decision === undefined ||
        decision.supported ||
        decision.code !== "" ||
        persisted
    );
};

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
    extraClassName?: string;
    dataAttrs: string;
    edgeAttr: string;
    labelClass: string;
    label: string;
    title: string;
    ariaLabel: string;
    startSeconds: number;
    lengthSeconds: number;
    durationSeconds: number;
    decoration?: string;
}): string => {
    const { left, width, empty } = spanGeometry(
        options.startSeconds,
        options.lengthSeconds,
        options.durationSeconds,
    );
    if (empty) {
        return "";
    }
    return (
        `<div class="${options.className}${options.extraClassName ? ` ${options.extraClassName}` : ""}" ${options.dataAttrs} style="left:${left}%;width:${width}%" role="button" tabindex="0" title="${escapeHtml(options.title)}" aria-label="${escapeHtml(options.ariaLabel)}">` +
        `<span class="${options.className}-resize ${options.className}-resize-l" ${options.edgeAttr}="left" aria-hidden="true"></span>` +
        (options.decoration ?? "") +
        `<span class="${options.labelClass}">${escapeHtml(options.label)}</span>` +
        `<span class="${options.className}-resize ${options.className}-resize-r" ${options.edgeAttr}="right" aria-hidden="true"></span>` +
        `</div>`
    );
};

export const headTag = (
    kind: string,
    label: string,
    options?: {
        active?: boolean;
        muted?: boolean;
        style?: string;
        /** Attributes that turn the tag into a button; decorative otherwise. */
        action?: string;
    },
): string => {
    const action = options?.action;
    const classes =
        `vst-head-tag vst-head-tag-${kind}` +
        (options?.active ? " vst-head-tag-active" : "") +
        (options?.muted ? " vst-head-tag-muted" : "") +
        (action ? " vst-head-tag-action" : "");
    const style = options?.style ? ` style="${options.style}"` : "";
    return (
        `<div class="${classes}"${style} ${action ? `${action} role="button" tabindex="0"` : `aria-hidden="true"`}>` +
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
    (tags ? `<div class="vst-head-tags">${tags}</div>` : "") +
    `</div>`;
