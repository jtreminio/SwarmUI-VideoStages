import { clamp, PROMPT_WINDOW_MIN_DURATION } from "./constants";
import { freeIntervalAt, type Span } from "./intervals";
import { clipDurationOf } from "./trackDomUtils";
import type { Clip, PromptWindow } from "./types";
import { roundToTenth } from "./utils";

/** The other prompt windows as clip-clamped, sorted spans (for interval walls). */
export const otherSpans = (
    windows: PromptWindow[],
    excludeIdx: number,
    clipDuration: number,
): Span[] =>
    windows
        .map((w, k) => ({
            k,
            start: clamp(w.start, 0, clipDuration),
            end: clamp(w.start + w.duration, 0, clipDuration),
        }))
        .filter((s) => s.k !== excludeIdx && s.end > s.start)
        .sort((a, b) => a.start - b.start)
        .map((s) => ({ start: s.start, end: s.end }));

/**
 * Move a prompt window's BEGIN edge to `desiredBegin` (seconds), keeping its end
 * fixed — the exact rule the timeline's left-edge resize gesture applies
 * (see the track config's `resizeTarget`, edge "left"): clamp the new start into
 * the free interval ending at this window's end (so it can't cross an adjacent
 * window or leave the clip) with a minimum-duration floor, then recompute
 * duration. Mutates in place.
 */
export const applyPromptWindowBegin = (
    clip: Clip,
    windowIdx: number,
    desiredBegin: number,
): void => {
    const window = clip.promptWindows?.[windowIdx];
    if (!window) {
        return;
    }
    const clipDur = clipDurationOf(clip);
    const end = window.start + window.duration;
    const spans = otherSpans(clip.promptWindows, windowIdx, clipDur);
    const [lo] = freeIntervalAt(spans, clipDur, Math.max(0, end - 1e-3));
    const start = clamp(desiredBegin, lo, end - PROMPT_WINDOW_MIN_DURATION);
    window.start = roundToTenth(start);
    window.duration = roundToTenth(end - start);
};

/**
 * Move a prompt window's END edge to `desiredEnd` (seconds), keeping its start
 * fixed — the exact rule the timeline's right-edge resize gesture applies
 * (see the track config's `resizeTarget`, edge "right"): clamp the new end into
 * the free interval starting at this window's start with a minimum-duration
 * floor, then recompute duration. Mutates in place.
 */
export const applyPromptWindowEnd = (
    clip: Clip,
    windowIdx: number,
    desiredEnd: number,
): void => {
    const window = clip.promptWindows?.[windowIdx];
    if (!window) {
        return;
    }
    const clipDur = clipDurationOf(clip);
    const spans = otherSpans(clip.promptWindows, windowIdx, clipDur);
    const [, hi] = freeIntervalAt(spans, clipDur, window.start);
    const end = clamp(
        desiredEnd,
        window.start + PROMPT_WINDOW_MIN_DURATION,
        hi,
    );
    window.start = roundToTenth(window.start);
    window.duration = roundToTenth(end - window.start);
};

/**
 * NEIGHBOUR bounds for a window's begin/end edges — the same interval walls
 * applyPromptWindowBegin/End clamp against. The dock's begin/end number inputs
 * use these as min (begin) / max (end) so spinner arrows stop AT a
 * neighbouring window instead of marching past it and snapping back on commit.
 * Only the neighbour walls belong in static input attributes: neighbours move
 * via gestures/deletes that rebuild the panel (fresh attrs), whereas the
 * window's OWN begin↔end coupling changes on value-only edits that don't
 * rebuild — that coupling stays with the commit clamp + display write-back.
 */
export const promptWindowNeighborBounds = (
    clip: Clip,
    windowIdx: number,
): { beginMin: number; endMax: number } | null => {
    const window = clip.promptWindows?.[windowIdx];
    if (!window) {
        return null;
    }
    const clipDur = clipDurationOf(clip);
    const end = window.start + window.duration;
    const spans = otherSpans(clip.promptWindows, windowIdx, clipDur);
    const [lo] = freeIntervalAt(spans, clipDur, Math.max(0, end - 1e-3));
    const [, hi] = freeIntervalAt(spans, clipDur, window.start);
    return { beginMin: roundToTenth(lo), endMax: roundToTenth(hi) };
};
