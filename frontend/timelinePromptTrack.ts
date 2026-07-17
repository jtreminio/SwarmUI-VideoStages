import {
    clamp,
    PROMPT_WINDOW_DEFAULT_DURATION,
    PROMPT_WINDOW_MIN_DURATION,
} from "./constants";
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
    startX: number;
    startStart: number;
    duration: number;
    clipDuration: number;
    boundLo: number;
    boundHi: number;
    originalLeft: string;
    active: boolean;
    sourceJson: string;
}

interface ResizeState {
    clipIdx: number;
    windowIdx: number;
    edge: "left" | "right";
    el: HTMLElement;
    startX: number;
    startStart: number;
    startDuration: number;
    clipDuration: number;
    originalLeft: string;
    originalWidth: string;
    active: boolean;
    sourceJson: string;
}

interface CreateState {
    clipIdx: number;
    lane: HTMLElement;
    laneLeft: number;
    startSec: number;
    startX: number;
    clipDuration: number;
    ghost: HTMLElement | null;
    active: boolean;
    sourceJson: string;
}

export interface TimelinePromptTrack {
    attach(body: HTMLElement): void;
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

interface Span {
    start: number;
    end: number;
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

const freeIntervalAt = (
    spans: Span[],
    clipDuration: number,
    point: number,
): [number, number] => {
    const p = clamp(point, 0, clipDuration);
    let lo = 0;
    let hi = clipDuration;
    for (const span of spans) {
        if (span.end <= p) {
            if (span.end > lo) {
                lo = span.end;
            }
        } else if (span.start >= p) {
            hi = span.start;
            break;
        } else {
            return [p, p];
        }
    }
    return [lo, hi];
};

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

export const createTimelinePromptTrack = (): TimelinePromptTrack => {
    let moveState: MoveState | null = null;
    let resizeState: ResizeState | null = null;
    let createState: CreateState | null = null;
    let suppressClick = false;
    let boundBody: HTMLElement | null = null;

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
        saveClips(clips);
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
        saveClips(clips);
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
        saveClips(clips);
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
        saveClips(clips);
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

    const clearGesture = (body: HTMLElement): void => {
        if (createState?.ghost) {
            createState.ghost.remove();
        }
        moveState = null;
        resizeState = null;
        createState = null;
        body.classList.remove(DRAGGING_CLASS);
    };

    const onBodyMouseDown = (event: Event): void => {
        suppressClick = false;
        const me = event as MouseEvent;
        if (me.button !== 0 || !(me.target instanceof Element)) {
            return;
        }
        if (me.target.closest(MINOR_ACTION_SELECTOR)) {
            return;
        }
        if (me.shiftKey && me.target.closest(MINOR_SELECTOR)) {
            me.preventDefault();
            return;
        }
        const edgeEl = me.target.closest(MINOR_EDGE_SELECTOR);
        if (edgeEl) {
            const seg = edgeEl.closest(MINOR_SELECTOR);
            const clipIdx = parseIntAttr(seg, "data-clip-idx");
            const windowIdx = parseIntAttr(seg, "data-window-idx");
            const edge =
                edgeEl.getAttribute("data-vst-minor-edge") === "left"
                    ? "left"
                    : "right";
            if (
                clipIdx === null ||
                windowIdx === null ||
                !(seg instanceof HTMLElement)
            ) {
                return;
            }
            const window = getClips()[clipIdx]?.promptWindows?.[windowIdx];
            if (!window) {
                return;
            }
            resizeState = {
                clipIdx,
                windowIdx,
                edge,
                el: seg,
                startX: me.clientX,
                startStart: window.start,
                startDuration: window.duration,
                clipDuration: clipDurationOf(getClips()[clipIdx]),
                originalLeft: seg.style.left,
                originalWidth: seg.style.width,
                active: false,
                sourceJson: readStateToken(),
            };
            me.preventDefault();
            return;
        }
        const seg = me.target.closest(MINOR_SELECTOR);
        if (seg instanceof HTMLElement) {
            const clipIdx = parseIntAttr(seg, "data-clip-idx");
            const windowIdx = parseIntAttr(seg, "data-window-idx");
            if (clipIdx === null || windowIdx === null) {
                return;
            }
            const clip = getClips()[clipIdx];
            const window = clip?.promptWindows?.[windowIdx];
            if (!clip || !window) {
                return;
            }
            const clipDuration = clipDurationOf(clip);
            const [boundLo, boundHi] = freeIntervalAt(
                otherSpans(clip.promptWindows, windowIdx, clipDuration),
                clipDuration,
                window.start,
            );
            moveState = {
                clipIdx,
                windowIdx,
                el: seg,
                startX: me.clientX,
                startStart: window.start,
                duration: window.duration,
                clipDuration,
                boundLo,
                boundHi,
                originalLeft: seg.style.left,
                active: false,
                sourceJson: readStateToken(),
            };
            me.preventDefault();
            return;
        }
        const lane = me.target.closest(LANE_SELECTOR);
        if (lane instanceof HTMLElement) {
            const clipIdx = parseIntAttr(lane, "data-clip-idx");
            if (clipIdx === null) {
                return;
            }
            const rect = lane.getBoundingClientRect();
            const pps = livePxPerSecond(boundBody ?? lane);
            const clipDuration = clipDurationOf(getClips()[clipIdx]);
            const startSec = clamp(
                (me.clientX - rect.left) / pps,
                0,
                clipDuration,
            );
            createState = {
                clipIdx,
                lane,
                laneLeft: rect.left,
                startSec,
                startX: me.clientX,
                clipDuration,
                ghost: null,
                active: false,
                sourceJson: readStateToken(),
            };
            me.preventDefault();
        }
    };

    const onDocMouseMove = (body: HTMLElement, event: Event): void => {
        const me = event as MouseEvent;
        const pps = livePxPerSecond(body);
        if (resizeState) {
            const dx = me.clientX - resizeState.startX;
            if (!resizeState.active && Math.abs(dx) < DRAG_THRESHOLD_PX) {
                return;
            }
            resizeState.active = true;
            body.classList.add(DRAGGING_CLASS);
            const clipDur = resizeState.clipDuration;
            const deltaSec = dx / pps;
            if (resizeState.edge === "right") {
                const end = clamp(
                    resizeState.startStart +
                        resizeState.startDuration +
                        deltaSec,
                    resizeState.startStart + PROMPT_WINDOW_MIN_DURATION,
                    clipDur,
                );
                resizeState.el.style.width = `${Math.max(2, (end - resizeState.startStart) * pps)}px`;
            } else {
                const end = resizeState.startStart + resizeState.startDuration;
                const start = clamp(
                    resizeState.startStart + deltaSec,
                    0,
                    end - PROMPT_WINDOW_MIN_DURATION,
                );
                resizeState.el.style.left = `${start * pps}px`;
                resizeState.el.style.width = `${Math.max(2, (end - start) * pps)}px`;
            }
            return;
        }
        if (moveState) {
            const dx = me.clientX - moveState.startX;
            if (!moveState.active && Math.abs(dx) < DRAG_THRESHOLD_PX) {
                return;
            }
            moveState.active = true;
            body.classList.add(DRAGGING_CLASS);
            const dur = Math.min(moveState.duration, moveState.clipDuration);
            const maxStart = Math.max(
                moveState.boundLo,
                moveState.boundHi - dur,
            );
            const start = clamp(
                moveState.startStart + dx / pps,
                moveState.boundLo,
                maxStart,
            );
            moveState.el.style.left = `${start * pps}px`;
            return;
        }
        if (createState) {
            const dx = me.clientX - createState.startX;
            if (!createState.active && Math.abs(dx) < DRAG_THRESHOLD_PX) {
                return;
            }
            createState.active = true;
            body.classList.add(DRAGGING_CLASS);
            const nowSec = laneTimeAt(createState, me.clientX, pps);
            const a = Math.min(createState.startSec, nowSec);
            const b = Math.max(createState.startSec, nowSec);
            if (!createState.ghost) {
                const ghost = document.createElement("div");
                ghost.className = GHOST_CLASS;
                createState.lane.appendChild(ghost);
                createState.ghost = ghost;
            }
            createState.ghost.style.left = `${a * pps}px`;
            createState.ghost.style.width = `${Math.max(2, (b - a) * pps)}px`;
        }
    };

    const onDocMouseUp = (body: HTMLElement, event: Event): void => {
        const me = event as MouseEvent;
        const pps = livePxPerSecond(body);
        if (resizeState) {
            const state = resizeState;
            resizeState = null;
            body.classList.remove(DRAGGING_CLASS);
            if (state.active) {
                suppressClick = true;
                commitResize(state, me.clientX - state.startX, pps);
            } else {
                state.el.style.left = state.originalLeft;
                state.el.style.width = state.originalWidth;
            }
            return;
        }
        if (moveState) {
            const state = moveState;
            moveState = null;
            body.classList.remove(DRAGGING_CLASS);
            if (state.active) {
                suppressClick = true;
                commitMove(state, me.clientX - state.startX, pps);
            } else {
                state.el.style.left = state.originalLeft;
            }
            return;
        }
        if (createState) {
            const state = createState;
            createState = null;
            body.classList.remove(DRAGGING_CLASS);
            if (state.ghost) {
                state.ghost.remove();
            }
            suppressClick = true;
            if (state.active) {
                commitCreate(state, laneTimeAt(state, me.clientX, pps));
            } else {
                commitCreate(state, null);
            }
        }
    };

    const onBodyClick = (event: Event): void => {
        if (suppressClick) {
            suppressClick = false;
            return;
        }
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

    const onDocKeyDown = (body: HTMLElement, event: KeyboardEvent): void => {
        if (event.key !== "Escape") {
            return;
        }
        if (resizeState) {
            resizeState.el.style.left = resizeState.originalLeft;
            resizeState.el.style.width = resizeState.originalWidth;
        } else if (moveState) {
            moveState.el.style.left = moveState.originalLeft;
        } else if (!createState) {
            return;
        }
        clearGesture(body);
    };

    let moveHandler: ((event: Event) => void) | null = null;
    let upHandler: ((event: Event) => void) | null = null;
    let keyHandler: ((event: KeyboardEvent) => void) | null = null;

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("mousedown", onBodyMouseDown);
        body.addEventListener("click", onBodyClick);
        moveHandler = (event) => onDocMouseMove(body, event);
        upHandler = (event) => onDocMouseUp(body, event);
        keyHandler = (event) => onDocKeyDown(body, event);
        document.addEventListener("mousemove", moveHandler);
        document.addEventListener("mouseup", upHandler);
        document.addEventListener("keydown", keyHandler);
    };

    const dispose = (): void => {
        if (boundBody) {
            boundBody.removeEventListener("mousedown", onBodyMouseDown);
            boundBody.removeEventListener("click", onBodyClick);
        }
        if (moveHandler) {
            document.removeEventListener("mousemove", moveHandler);
            moveHandler = null;
        }
        if (upHandler) {
            document.removeEventListener("mouseup", upHandler);
            upHandler = null;
        }
        if (keyHandler) {
            document.removeEventListener("keydown", keyHandler);
            keyHandler = null;
        }
        moveState = null;
        resizeState = null;
        createState = null;
        boundBody = null;
    };

    return { attach, dispose };
};
