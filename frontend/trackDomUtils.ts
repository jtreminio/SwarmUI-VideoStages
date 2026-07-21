/**
 * Small DOM/model helpers shared by the timeline track modules
 */

import { clamp } from "./constants";
import { getClips, saveClips } from "./persistence";
import { getRootDefaults } from "./rootDefaults";
import type { UpdateOrigin } from "./store";
import { readStateToken } from "./swarmInputs";
import { DEFAULT_PX_PER_SECOND } from "./timelineView";
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

/** Left offset of a span inside its clip lane, as a CSS percentage. */
export const leftPct = (start: number, duration: number): number =>
    duration > 0 ? (clamp(start, 0, duration) / duration) * 100 : 0;

/** Width of a span inside its clip lane, as a CSS percentage. */
export const widthPct = (length: number, duration: number): number =>
    duration > 0 ? (clamp(length, 0, duration) / duration) * 100 : 0;

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

/** The timeline's fps for frame math, with the 24fps fallback. */
export const currentTimelineFps = (): number => {
    try {
        const fps = getRootDefaults().fps;
        return typeof fps === "number" && fps > 0 ? fps : 24;
    } catch {
        return 24;
    }
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
