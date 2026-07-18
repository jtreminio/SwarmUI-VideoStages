import {
    clamp,
    PROMPT_WINDOW_DEFAULT_DURATION,
    PROMPT_WINDOW_MIN_DURATION,
} from "./constants";
import {
    claimOnly,
    type GestureRouter,
    type GestureSession,
} from "./gestureRouter";
import { freeIntervalAt, type Span } from "./intervals";
import { getClips, saveClips } from "./persistence";
import { readStateToken } from "./swarmInputs";
import { livePxPerSecond } from "./timelineLinking";
import type { Clip, PromptWindow } from "./types";
import { setSelection } from "./uiState";

const MAJOR_SELECTOR = ".vst-major-seg[data-clip-idx]";
const MINOR_SELECTOR = ".vst-minor-seg[data-clip-idx]";
const MINOR_EDGE_SELECTOR = "[data-vst-minor-edge]";
const MINOR_ACTION_SELECTOR = "[data-vst-minor-action]";
const LANE_SELECTOR = ".vst-minor-lane[data-clip-idx]";

const DRAG_THRESHOLD_PX = 4;
const DRAGGING_CLASS = "vst-prompt-dragging";
const GHOST_CLASS = "vst-minor-ghost";

interface MoveState {
    clipIdx: number;
    windowIdx: number;
    el: HTMLElement;
    startStart: number;
    duration: number;
    clipDuration: number;
    boundLo: number;
    boundHi: number;
    originalLeft: string;
    sourceJson: string;
}

interface ResizeState {
    clipIdx: number;
    windowIdx: number;
    edge: "left" | "right";
    el: HTMLElement;
    startStart: number;
    startDuration: number;
    clipDuration: number;
    originalLeft: string;
    originalWidth: string;
    sourceJson: string;
}

interface CreateState {
    clipIdx: number;
    lane: HTMLElement;
    laneLeft: number;
    startSec: number;
    clipDuration: number;
    ghost: HTMLElement | null;
    sourceJson: string;
}

export interface TimelinePromptTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

const parseIntAttr = (el: Element | null, name: string): number | null => {
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

const clipDurationOf = (clip: Clip | undefined): number =>
    clip ? Math.max(0, clip.duration || 0) : 0;

const roundSeconds = (seconds: number): number => Math.round(seconds * 10) / 10;

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
 * (see `commitResize`, edge "left"): clamp the new start into the free interval
 * ending at this window's end (so it can't cross an adjacent window or leave the
 * clip) with a minimum-duration floor, then recompute duration. Mutates in place.
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
    window.start = roundSeconds(start);
    window.duration = roundSeconds(end - start);
};

/**
 * Move a prompt window's END edge to `desiredEnd` (seconds), keeping its start
 * fixed — the exact rule the timeline's right-edge resize gesture applies
 * (see `commitResize`, edge "right"): clamp the new end into the free interval
 * starting at this window's start with a minimum-duration floor, then recompute
 * duration. Mutates in place.
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
    window.start = roundSeconds(window.start);
    window.duration = roundSeconds(end - window.start);
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
    return { beginMin: roundSeconds(lo), endMax: roundSeconds(hi) };
};

export const createTimelinePromptTrack = (): TimelinePromptTrack => {
    let boundBody: HTMLElement | null = null;
    let unregister: (() => void) | null = null;

    const isStale = (sourceJson: string): boolean =>
        readStateToken() !== sourceJson;

    const applyMinorAction = (
        clipIdx: number,
        windowIdx: number,
        action: string,
    ): void => {
        const clips = getClips();
        const clip = clips[clipIdx];
        const window = clip?.promptWindows?.[windowIdx];
        if (!clip || !window) {
            return;
        }
        if (action !== "delete") {
            return;
        }
        clip.promptWindows.splice(windowIdx, 1);
        saveClips(clips, undefined, { origin: "prompt-track" });
    };

    const commitMove = (state: MoveState, dxPx: number, pps: number): void => {
        if (isStale(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        const window = clip?.promptWindows?.[state.windowIdx];
        if (!clip || !window) {
            return;
        }
        const clipDur = clipDurationOf(clip);
        const desiredStart = state.startStart + dxPx / pps;
        const dur = Math.min(state.duration, clipDur);
        const maxStart = Math.max(state.boundLo, state.boundHi - dur);
        window.start = roundSeconds(
            clamp(desiredStart, state.boundLo, maxStart),
        );
        window.duration = roundSeconds(
            Math.max(
                PROMPT_WINDOW_MIN_DURATION,
                Math.min(dur, state.boundHi - window.start),
            ),
        );
        saveClips(clips, undefined, { origin: "prompt-track" });
        // The dragged window becomes the selection. Must run AFTER the save:
        // the save's dock rebuild restores focus to whichever editor owned it,
        // and that editor's focus handler would re-point the selection to ITS
        // window, hijacking the drag.
        setSelection({
            kind: "prompt-minor",
            clipIdx: state.clipIdx,
            windowIdx: state.windowIdx,
        });
    };

    const commitResize = (
        state: ResizeState,
        dxPx: number,
        pps: number,
    ): void => {
        if (isStale(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        const window = clip?.promptWindows?.[state.windowIdx];
        if (!clip || !window) {
            return;
        }
        const clipDur = clipDurationOf(clip);
        const spans = otherSpans(clip.promptWindows, state.windowIdx, clipDur);
        const deltaSec = dxPx / pps;
        if (state.edge === "right") {
            const [, hi] = freeIntervalAt(spans, clipDur, state.startStart);
            const end = clamp(
                state.startStart + state.startDuration + deltaSec,
                state.startStart + PROMPT_WINDOW_MIN_DURATION,
                hi,
            );
            window.start = roundSeconds(state.startStart);
            window.duration = roundSeconds(end - state.startStart);
        } else {
            const end = state.startStart + state.startDuration;
            const [lo] = freeIntervalAt(
                spans,
                clipDur,
                Math.max(0, end - 1e-3),
            );
            const start = clamp(
                state.startStart + deltaSec,
                lo,
                end - PROMPT_WINDOW_MIN_DURATION,
            );
            window.start = roundSeconds(start);
            window.duration = roundSeconds(end - start);
        }
        saveClips(clips, undefined, { origin: "prompt-track" });
        // See commitMove: select the resized window, after the save.
        setSelection({
            kind: "prompt-minor",
            clipIdx: state.clipIdx,
            windowIdx: state.windowIdx,
        });
    };

    const commitCreate = (state: CreateState, endSec: number | null): void => {
        if (isStale(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        if (!clip) {
            return;
        }
        const clipDur = clipDurationOf(clip);
        const spans = otherSpans(clip.promptWindows, -1, clipDur);
        const [lo, hi] = freeIntervalAt(spans, clipDur, state.startSec);
        const gap = hi - lo;
        if (gap < PROMPT_WINDOW_MIN_DURATION) {
            return;
        }
        let start: number;
        let duration: number;
        if (endSec === null) {
            duration = Math.min(PROMPT_WINDOW_DEFAULT_DURATION, gap);
            start = clamp(state.startSec, lo, hi - duration);
        } else {
            const a = clamp(Math.min(state.startSec, endSec), lo, hi);
            const b = clamp(Math.max(state.startSec, endSec), lo, hi);
            start = a;
            duration = Math.max(PROMPT_WINDOW_MIN_DURATION, b - a);
            if (start + duration > hi) {
                duration = hi - start;
            }
        }
        if (duration < PROMPT_WINDOW_MIN_DURATION) {
            return;
        }
        const window: PromptWindow = {
            prompt: "",
            start: roundSeconds(start),
            duration: roundSeconds(duration),
        };
        clip.promptWindows.push(window);
        clip.promptWindows.sort((x, y) => x.start - y.start);
        saveClips(clips, undefined, { origin: "prompt-track" });
        // Open the new window in the dock ready to type: selecting it auto-expands
        // the dock (via the strip's selection subscriber) and focuses its editor.
        const newIdx = clip.promptWindows.indexOf(window);
        if (newIdx >= 0) {
            setSelection({
                kind: "prompt-minor",
                clipIdx: state.clipIdx,
                windowIdx: newIdx,
            });
        }
    };

    const laneTimeAt = (
        state: CreateState,
        clientX: number,
        pps: number,
    ): number => clamp((clientX - state.laneLeft) / pps, 0, state.clipDuration);

    const resizeSession = (
        body: HTMLElement,
        state: ResizeState,
    ): GestureSession => {
        const restore = (): void => {
            state.el.style.left = state.originalLeft;
            state.el.style.width = state.originalWidth;
        };
        return {
            threshold: DRAG_THRESHOLD_PX,
            onMove: (ctx) => {
                body.classList.add(DRAGGING_CLASS);
                const pps = livePxPerSecond(body);
                const clipDur = state.clipDuration;
                const deltaSec = ctx.dx / pps;
                if (state.edge === "right") {
                    const end = clamp(
                        state.startStart + state.startDuration + deltaSec,
                        state.startStart + PROMPT_WINDOW_MIN_DURATION,
                        clipDur,
                    );
                    state.el.style.width = `${Math.max(2, (end - state.startStart) * pps)}px`;
                } else {
                    const end = state.startStart + state.startDuration;
                    const start = clamp(
                        state.startStart + deltaSec,
                        0,
                        end - PROMPT_WINDOW_MIN_DURATION,
                    );
                    state.el.style.left = `${start * pps}px`;
                    state.el.style.width = `${Math.max(2, (end - start) * pps)}px`;
                }
            },
            onCommit: (ctx) => {
                body.classList.remove(DRAGGING_CLASS);
                commitResize(state, ctx.dx, livePxPerSecond(body));
            },
            onTap: restore,
            onCancel: () => {
                restore();
                body.classList.remove(DRAGGING_CLASS);
            },
        };
    };

    const moveSession = (
        body: HTMLElement,
        state: MoveState,
    ): GestureSession => {
        const restore = (): void => {
            state.el.style.left = state.originalLeft;
        };
        return {
            threshold: DRAG_THRESHOLD_PX,
            onMove: (ctx) => {
                body.classList.add(DRAGGING_CLASS);
                const pps = livePxPerSecond(body);
                const dur = Math.min(state.duration, state.clipDuration);
                const maxStart = Math.max(state.boundLo, state.boundHi - dur);
                const start = clamp(
                    state.startStart + ctx.dx / pps,
                    state.boundLo,
                    maxStart,
                );
                state.el.style.left = `${start * pps}px`;
            },
            onCommit: (ctx) => {
                body.classList.remove(DRAGGING_CLASS);
                commitMove(state, ctx.dx, livePxPerSecond(body));
            },
            onTap: restore,
            onCancel: () => {
                restore();
                body.classList.remove(DRAGGING_CLASS);
            },
        };
    };

    const createSession = (
        body: HTMLElement,
        state: CreateState,
    ): GestureSession => {
        const removeGhost = (): void => {
            state.ghost?.remove();
            state.ghost = null;
        };
        return {
            threshold: DRAG_THRESHOLD_PX,
            // A plain lane tap creates a default-length window at the pressed
            // time, so the concluding click is always consumed — as before.
            suppressTapClick: true,
            onMove: (ctx) => {
                body.classList.add(DRAGGING_CLASS);
                const pps = livePxPerSecond(body);
                const nowSec = laneTimeAt(state, ctx.event.clientX, pps);
                const a = Math.min(state.startSec, nowSec);
                const b = Math.max(state.startSec, nowSec);
                if (!state.ghost) {
                    const ghost = document.createElement("div");
                    ghost.className = GHOST_CLASS;
                    state.lane.appendChild(ghost);
                    state.ghost = ghost;
                }
                state.ghost.style.left = `${a * pps}px`;
                state.ghost.style.width = `${Math.max(2, (b - a) * pps)}px`;
            },
            onCommit: (ctx) => {
                body.classList.remove(DRAGGING_CLASS);
                removeGhost();
                commitCreate(
                    state,
                    laneTimeAt(state, ctx.event.clientX, livePxPerSecond(body)),
                );
            },
            onTap: () => {
                removeGhost();
                commitCreate(state, null);
            },
            onCancel: () => {
                removeGhost();
                body.classList.remove(DRAGGING_CLASS);
            },
        };
    };

    const onPress = (
        me: MouseEvent,
        body: HTMLElement,
    ): GestureSession | null => {
        if (!(me.target instanceof Element)) {
            return null;
        }
        if (me.target.closest(MINOR_ACTION_SELECTOR)) {
            return null;
        }
        if (me.shiftKey && me.target.closest(MINOR_SELECTOR)) {
            // The window owns this press; the shift-CLICK delete stays in
            // onBodyClick.
            me.preventDefault();
            return claimOnly();
        }
        const edgeEl = me.target.closest(MINOR_EDGE_SELECTOR);
        if (edgeEl) {
            const seg = edgeEl.closest(MINOR_SELECTOR);
            const clipIdx = parseIntAttr(seg, "data-clip-idx");
            const windowIdx = parseIntAttr(seg, "data-window-idx");
            if (
                clipIdx === null ||
                windowIdx === null ||
                !(seg instanceof HTMLElement)
            ) {
                return null;
            }
            const window = getClips()[clipIdx]?.promptWindows?.[windowIdx];
            if (!window) {
                return null;
            }
            me.preventDefault();
            return resizeSession(body, {
                clipIdx,
                windowIdx,
                edge:
                    edgeEl.getAttribute("data-vst-minor-edge") === "left"
                        ? "left"
                        : "right",
                el: seg,
                startStart: window.start,
                startDuration: window.duration,
                clipDuration: clipDurationOf(getClips()[clipIdx]),
                originalLeft: seg.style.left,
                originalWidth: seg.style.width,
                sourceJson: readStateToken(),
            });
        }
        const seg = me.target.closest(MINOR_SELECTOR);
        if (seg instanceof HTMLElement) {
            const clipIdx = parseIntAttr(seg, "data-clip-idx");
            const windowIdx = parseIntAttr(seg, "data-window-idx");
            if (clipIdx === null || windowIdx === null) {
                return null;
            }
            const clip = getClips()[clipIdx];
            const window = clip?.promptWindows?.[windowIdx];
            if (!clip || !window) {
                return null;
            }
            const clipDuration = clipDurationOf(clip);
            const [boundLo, boundHi] = freeIntervalAt(
                otherSpans(clip.promptWindows, windowIdx, clipDuration),
                clipDuration,
                window.start,
            );
            me.preventDefault();
            return moveSession(body, {
                clipIdx,
                windowIdx,
                el: seg,
                startStart: window.start,
                duration: window.duration,
                clipDuration,
                boundLo,
                boundHi,
                originalLeft: seg.style.left,
                sourceJson: readStateToken(),
            });
        }
        const lane = me.target.closest(LANE_SELECTOR);
        if (lane instanceof HTMLElement) {
            const clipIdx = parseIntAttr(lane, "data-clip-idx");
            if (clipIdx === null) {
                return null;
            }
            const rect = lane.getBoundingClientRect();
            const pps = livePxPerSecond(body);
            const clipDuration = clipDurationOf(getClips()[clipIdx]);
            const startSec = clamp(
                (me.clientX - rect.left) / pps,
                0,
                clipDuration,
            );
            me.preventDefault();
            return createSession(body, {
                clipIdx,
                lane,
                laneLeft: rect.left,
                startSec,
                clipDuration,
                ghost: null,
                sourceJson: readStateToken(),
            });
        }
        return null;
    };

    const onBodyClick = (event: Event): void => {
        if (!(event.target instanceof Element)) {
            return;
        }
        const actionEl = event.target.closest(MINOR_ACTION_SELECTOR);
        if (actionEl) {
            const seg = actionEl.closest(MINOR_SELECTOR);
            const clipIdx = parseIntAttr(seg, "data-clip-idx");
            const windowIdx = parseIntAttr(seg, "data-window-idx");
            const action = actionEl.getAttribute("data-vst-minor-action") ?? "";
            if (clipIdx !== null && windowIdx !== null) {
                applyMinorAction(clipIdx, windowIdx, action);
            }
            return;
        }
        const minor = event.target.closest(MINOR_SELECTOR);
        if (minor instanceof HTMLElement) {
            const clipIdx = parseIntAttr(minor, "data-clip-idx");
            const windowIdx = parseIntAttr(minor, "data-window-idx");
            if (clipIdx === null || windowIdx === null) {
                return;
            }
            if ((event as MouseEvent).shiftKey) {
                applyMinorAction(clipIdx, windowIdx, "delete");
                return;
            }
            const window = getClips()[clipIdx]?.promptWindows?.[windowIdx];
            if (!window) {
                return;
            }
            setSelection({ kind: "prompt-minor", clipIdx, windowIdx });
            return;
        }
        const major = event.target.closest(MAJOR_SELECTOR);
        if (major instanceof HTMLElement) {
            const clipIdx = parseIntAttr(major, "data-clip-idx");
            if (clipIdx === null) {
                return;
            }
            if (!getClips()[clipIdx]) {
                return;
            }
            setSelection({ kind: "prompt-major", clipIdx });
        }
    };

    const attach = (body: HTMLElement, router: GestureRouter): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        unregister = router.register({
            id: "prompt-track",
            priority: 20,
            onPress,
        });
    };

    const dispose = (): void => {
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
        }
        unregister?.();
        unregister = null;
        boundBody = null;
    };

    return { attach, dispose };
};
