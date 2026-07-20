import {
    clamp,
    PROMPT_WINDOW_DEFAULT_DURATION,
    PROMPT_WINDOW_MIN_DURATION,
} from "./constants";
import type { GestureRouter } from "./gestureRouter";
import { freeIntervalAt, type Span } from "./intervals";
import { getClips } from "./persistence";
import { clipDurationOf, parseIntAttr } from "./trackDomUtils";
import type { Clip, PromptWindow } from "./types";
import { setSelection } from "./uiState";
import { roundToTenth } from "./utils";
import {
    createWindowTrack,
    type PressSpan,
    type SpanGeom,
} from "./windowTrack";

const MAJOR_SELECTOR = ".vst-major-seg[data-clip-idx]";

export interface TimelinePromptTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

const otherSpans = (
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

/** The free-interval walls around a window at its press-time position. */
const wallsFor = (
    clip: Clip,
    windowIdx: number,
    press: PressSpan,
): [number, number] => {
    const clipDur = clipDurationOf(clip);
    return freeIntervalAt(
        otherSpans(clip.promptWindows ?? [], windowIdx, clipDur),
        clipDur,
        press.start,
    );
};

/**
 * The two-part Prompt track's MINOR relay-window lane. Windows must not
 * overlap: moves and resizes clamp to the free interval between neighbouring
 * windows (`intervals.ts`), and creates only land in a gap wide enough for a
 * minimum-length window.
 */
export const createTimelinePromptTrack = (): TimelinePromptTrack =>
    createWindowTrack({
        routeId: "prompt-track",
        priority: 20,
        origin: "prompt-track",
        spanSelector: ".vst-minor-seg[data-clip-idx]",
        itemIdxAttr: "data-window-idx",
        edgeSelector: "[data-vst-minor-edge]",
        edgeAttr: "data-vst-minor-edge",
        laneSelector: ".vst-minor-lane[data-clip-idx]",
        draggingClass: "vst-prompt-dragging",
        ghostClass: "vst-minor-ghost",
        unit: "px",
        keyboardSelect: false,
        isolateClicks: false,
        readSpan: (clip, windowIdx): PressSpan | null => {
            const window = clip.promptWindows?.[windowIdx];
            return window
                ? { start: window.start, length: window.duration, trim: 0 }
                : null;
        },
        moveTargetStart: (clip, windowIdx, press, desiredStart) => {
            const clipDur = clipDurationOf(clip);
            const [lo, hi] = wallsFor(clip, windowIdx, press);
            const dur = Math.min(press.length, clipDur);
            return clamp(desiredStart, lo, Math.max(lo, hi - dur));
        },
        writeMove: (clip, windowIdx, press, start) => {
            const window = clip.promptWindows?.[windowIdx];
            if (!window) {
                return;
            }
            const clipDur = clipDurationOf(clip);
            const [, hi] = wallsFor(clip, windowIdx, press);
            const dur = Math.min(press.length, clipDur);
            window.start = roundToTenth(start);
            window.duration = roundToTenth(
                Math.max(
                    PROMPT_WINDOW_MIN_DURATION,
                    Math.min(dur, hi - window.start),
                ),
            );
        },
        resizeTarget: (clip, windowIdx, edge, press, deltaSec): SpanGeom => {
            const clipDur = clipDurationOf(clip);
            const spans = otherSpans(
                clip.promptWindows ?? [],
                windowIdx,
                clipDur,
            );
            if (edge === "right") {
                const [, hi] = freeIntervalAt(spans, clipDur, press.start);
                const end = clamp(
                    press.start + press.length + deltaSec,
                    press.start + PROMPT_WINDOW_MIN_DURATION,
                    hi,
                );
                return { start: press.start, length: end - press.start };
            }
            const end = press.start + press.length;
            const [lo] = freeIntervalAt(
                spans,
                clipDur,
                Math.max(0, end - 1e-3),
            );
            const start = clamp(
                press.start + deltaSec,
                lo,
                end - PROMPT_WINDOW_MIN_DURATION,
            );
            return { start, length: end - start };
        },
        writeResize: (clip, windowIdx, _edge, _press, geom) => {
            const window = clip.promptWindows?.[windowIdx];
            if (!window) {
                return;
            }
            window.start = roundToTenth(geom.start);
            window.duration = roundToTenth(geom.length);
        },
        createSpan: (clip, clipIdx, startSec, endSec) => {
            const clipDur = clipDurationOf(clip);
            const spans = otherSpans(clip.promptWindows ?? [], -1, clipDur);
            const [lo, hi] = freeIntervalAt(spans, clipDur, startSec);
            const gap = hi - lo;
            if (gap < PROMPT_WINDOW_MIN_DURATION) {
                return null;
            }
            let start: number;
            let duration: number;
            if (endSec === null) {
                duration = Math.min(PROMPT_WINDOW_DEFAULT_DURATION, gap);
                start = clamp(startSec, lo, hi - duration);
            } else {
                const a = clamp(Math.min(startSec, endSec), lo, hi);
                const b = clamp(Math.max(startSec, endSec), lo, hi);
                start = a;
                duration = Math.max(PROMPT_WINDOW_MIN_DURATION, b - a);
                if (start + duration > hi) {
                    duration = hi - start;
                }
            }
            if (duration < PROMPT_WINDOW_MIN_DURATION) {
                return null;
            }
            const window: PromptWindow = {
                prompt: "",
                start: roundToTenth(start),
                duration: roundToTenth(duration),
            };
            clip.promptWindows.push(window);
            clip.promptWindows.sort((x, y) => x.start - y.start);
            // Open the new window in the dock ready to type: selecting it
            // auto-expands the dock (via the strip's selection subscriber) and
            // focuses its editor.
            const newIdx = clip.promptWindows.indexOf(window);
            return newIdx >= 0
                ? { kind: "prompt-minor", clipIdx, windowIdx: newIdx }
                : null;
        },
        deleteItem: (clip, windowIdx) => {
            if (!clip.promptWindows?.[windowIdx]) {
                return false;
            }
            clip.promptWindows.splice(windowIdx, 1);
            return true;
        },
        selectionFor: (clipIdx, windowIdx) => ({
            kind: "prompt-minor",
            clipIdx,
            windowIdx,
        }),
        // Clicks that land on the MAJOR (whole-clip prompt) row select it.
        onClickFallthrough: (_event, target) => {
            const major = target.closest(MAJOR_SELECTOR);
            if (!(major instanceof HTMLElement)) {
                return;
            }
            const clipIdx = parseIntAttr(major, "data-clip-idx");
            if (clipIdx === null || !getClips()[clipIdx]) {
                return;
            }
            setSelection({ kind: "prompt-major", clipIdx });
        },
    });
