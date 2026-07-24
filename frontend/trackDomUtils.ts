/**
 * Small DOM/model helpers shared by the timeline track modules
 */

import { clamp } from "./constants";
import { getClips, saveClips } from "./persistence";
import type { UpdateOrigin } from "./store";
import { readStateToken } from "./swarmInputs";
import { DEFAULT_PX_PER_SECOND } from "./timelineView/layout";
import type { Clip } from "./types";

/** The timeline body's live px-per-second (its zoom), with the default fallback. */
export const livePxPerSecond = (body: HTMLElement): number => {
    const pps = Number.parseFloat(body.dataset.vstPps ?? "");
    return Number.isFinite(pps) && pps > 0 ? pps : DEFAULT_PX_PER_SECOND;
};

export const parseIntAttr = (
    el: Element | null,
    name: string,
): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute(name);
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

export const clipDurationOf = (clip: Clip | undefined): number =>
    clip ? Math.max(0, clip.duration || 0) : 0;

export interface SpanGeometryOptions {
    /**
     * Output unit. `"percent"` is relative to the lane duration; `"px"` scales
     * by `pxPerSecond`.
     */
    unit?: "percent" | "px";
    pxPerSecond?: number;
    /** Minimum rendered width, in output units (e.g. a 2px hairline floor). */
    minWidth?: number;
    /**
     * Keep `left`/`width` inside the lane. Defaults to true for percent output,
     * because an unclamped percentage span renders outside its lane.
     */
    clampOutput?: boolean;
}

export interface SpanGeometry {
    /** Input span clamped into `[0, duration]`. */
    startSeconds: number;
    endSeconds: number;
    left: number;
    width: number;
    /** True when the clamped span has no extent, so there is nothing to draw. */
    empty: boolean;
}

/**
 * The single span → left/width projection for every timeline lane. Edge
 * behaviour that used to differ per call site (pixel vs percentage output, the
 * minimum-width floor, and the output clamp) is expressed as explicit options.
 */
export const spanGeometry = (
    startSeconds: number,
    lengthSeconds: number,
    durationSeconds: number,
    options: SpanGeometryOptions = {},
): SpanGeometry => {
    const duration = Math.max(0, durationSeconds || 0);
    const rawStart = startSeconds || 0;
    const start = clamp(rawStart, 0, duration);
    const end = clamp(rawStart + (lengthSeconds || 0), start, duration);
    const percent = (options.unit ?? "percent") === "percent";
    const scale = percent
        ? duration > 0
            ? 100 / duration
            : 0
        : (options.pxPerSecond ?? 0);
    let left = start * scale;
    let width = (end - start) * scale;
    if (options.clampOutput ?? percent) {
        const bound = percent ? 100 : Number.MAX_SAFE_INTEGER;
        left = clamp(left, 0, bound);
        width = clamp(width, 0, bound - left);
    }
    if (options.minWidth !== undefined) {
        width = Math.max(options.minWidth, width);
    }
    return {
        startSeconds: start,
        endSeconds: end,
        left,
        width,
        empty: end <= start,
    };
};

/** Left offset of a point inside its lane, as a CSS percentage. */
export const keyframeLeftPercent = (time: number, duration: number): number =>
    spanGeometry(time, 0, duration).left;

/**
 * True when the carriers changed since `sourceToken` was captured (the
 * gesture-commit stale guard: never write over someone else's newer state).
 */
export const isStaleToken = (sourceToken: string): boolean =>
    readStateToken() !== sourceToken;

/**
 * The clip-track gesture-commit skeleton: bail when the carriers changed since
 * the gesture began, otherwise run `mutate` on the live clips and save the
 * array it returns (or a new array, e.g. a reordered one) under `origin`. Any
 * selection change belongs inside `mutate`; returns true when a save happened.
 */
export const commitClipMutation = (
    sourceToken: string,
    origin: UpdateOrigin,
    mutate: (clips: Clip[]) => Clip[] | null,
): boolean => {
    if (isStaleToken(sourceToken)) {
        return false;
    }
    const next = mutate(getClips());
    if (!next) {
        return false;
    }
    saveClips(next, { origin });
    return true;
};

/**
 * Keyboard-activation check for click-selectable track elements: Enter or
 * Space.
 */
export const isActivateKey = (ke: KeyboardEvent): boolean =>
    ke.key === "Enter" || ke.key === " " || ke.key === "Spacebar";

/**
 * Shared wiring for a click-only selectable track (audio row, boundary
 * chips): a body click or Enter/Space on an element matching `selector`
 * invokes `activate` with that element. Returns the detach function.
 */
export const bindClickSelectableTrack = (
    body: HTMLElement,
    selector: string,
    activate: (el: HTMLElement) => void,
): (() => void) => {
    const fromTarget = (target: Element): void => {
        const el = target.closest(selector);
        if (el instanceof HTMLElement) {
            activate(el);
        }
    };
    const onClick = (event: Event): void => {
        if (event.target instanceof Element) {
            fromTarget(event.target);
        }
    };
    const onKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (!isActivateKey(ke)) {
            return;
        }
        if (!(ke.target instanceof Element) || !ke.target.closest(selector)) {
            return;
        }
        ke.preventDefault();
        fromTarget(ke.target);
    };
    body.addEventListener("click", onClick);
    body.addEventListener("keydown", onKeyDown);
    return () => {
        body.removeEventListener("click", onClick);
        body.removeEventListener("keydown", onKeyDown);
    };
};
