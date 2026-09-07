import { clipDurationOf } from "./clipSemantics";
import { clamp, PROMPT_WINDOW_MIN_DURATION } from "./constants";
import { freeIntervalAt, type Span } from "./intervals";
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

/** Keep the end fixed; clamp the start against neighbors and the minimum length. */
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

/** Keep the start fixed; clamp the end against neighbors and the clip end. */
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

/** Static input limits follow neighboring windows; live begin/end coupling stays in commit clamps. */
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
