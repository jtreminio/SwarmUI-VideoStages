import { clamp, RETAKE_MIN_DURATION } from "./constants";
import { getClips, saveClips } from "./persistence";
import { readStateToken } from "./swarmInputs";
import { livePxPerSecond } from "./timelineLinking";
import type { Clip } from "./types";
import { setSelection } from "./uiState";

const RETAKE_SELECTOR = ".vst-retake[data-clip-idx]";
const RETAKE_EDGE_SELECTOR = "[data-vst-retake-edge]";

const DRAG_THRESHOLD_PX = 4;
const DRAGGING_CLASS = "vst-retake-dragging";

interface MoveState {
    clipIdx: number;
    el: HTMLElement;
    startX: number;
    startStart: number;
    length: number;
    clipDuration: number;
    originalLeft: string;
    active: boolean;
    sourceJson: string;
}

interface ResizeState {
    clipIdx: number;
    edge: "left" | "right";
    el: HTMLElement;
    startX: number;
    startStart: number;
    startLength: number;
    clipDuration: number;
    originalLeft: string;
    originalWidth: string;
    active: boolean;
    sourceJson: string;
}

export interface TimelineRetakeTrack {
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

const leftPct = (start: number, duration: number): number =>
    duration > 0 ? (clamp(start, 0, duration) / duration) * 100 : 0;

const widthPct = (length: number, duration: number): number =>
    duration > 0 ? (clamp(length, 0, duration) / duration) * 100 : 0;

export const createTimelineRetakeTrack = (): TimelineRetakeTrack => {
    let moveState: MoveState | null = null;
    let resizeState: ResizeState | null = null;
    let suppressClick = false;
    let boundBody: HTMLElement | null = null;

    const isStale = (sourceJson: string): boolean =>
        readStateToken() !== sourceJson;

    const deleteRetake = (clipIdx: number): void => {
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip?.retake) {
            return;
        }
        clip.retake = null;
        saveClips(clips);
    };

    const commitMove = (state: MoveState, dxPx: number, pps: number): void => {
        if (isStale(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        if (!clip?.retake) {
            return;
        }
        const clipDur = clipDurationOf(clip);
        const length = Math.min(state.length, clipDur);
        const maxStart = Math.max(0, clipDur - length);
        const start = clamp(state.startStart + dxPx / pps, 0, maxStart);
        clip.retake.startSeconds = roundSeconds(start);
        clip.retake.lengthSeconds = roundSeconds(
            Math.min(length, clipDur - clip.retake.startSeconds),
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
        if (!clip?.retake) {
            return;
        }
        const clipDur = clipDurationOf(clip);
        const deltaSec = dxPx / pps;
        if (state.edge === "right") {
            const end = clamp(
                state.startStart + state.startLength + deltaSec,
                state.startStart + RETAKE_MIN_DURATION,
                clipDur,
            );
            clip.retake.startSeconds = roundSeconds(state.startStart);
            clip.retake.lengthSeconds = roundSeconds(end - state.startStart);
        } else {
            const end = state.startStart + state.startLength;
            const start = clamp(
                state.startStart + deltaSec,
                0,
                end - RETAKE_MIN_DURATION,
            );
            clip.retake.startSeconds = roundSeconds(start);
            clip.retake.lengthSeconds = roundSeconds(end - start);
        }
        saveClips(clips);
    };

    const clearGesture = (body: HTMLElement): void => {
        moveState = null;
        resizeState = null;
        body.classList.remove(DRAGGING_CLASS);
    };

    const onBodyMouseDown = (event: Event): void => {
        suppressClick = false;
        const me = event as MouseEvent;
        if (me.button !== 0 || !(me.target instanceof Element)) {
            return;
        }
        const overlay = me.target.closest(RETAKE_SELECTOR);
        if (!(overlay instanceof HTMLElement)) {
            return;
        }
        // Retake owns this gesture; keep the region drag/reorder in linking from also firing.
        me.stopImmediatePropagation();
        if (me.shiftKey) {
            me.preventDefault();
            return;
        }
        const clipIdx = parseIntAttr(overlay, "data-clip-idx");
        if (clipIdx === null) {
            return;
        }
        const clip = getClips()[clipIdx];
        if (!clip?.retake) {
            return;
        }
        const clipDuration = clipDurationOf(clip);
        const edgeEl = me.target.closest(RETAKE_EDGE_SELECTOR);
        if (edgeEl) {
            resizeState = {
                clipIdx,
                edge:
                    edgeEl.getAttribute("data-vst-retake-edge") === "left"
                        ? "left"
                        : "right",
                el: overlay,
                startX: me.clientX,
                startStart: clip.retake.startSeconds,
                startLength: clip.retake.lengthSeconds,
                clipDuration,
                originalLeft: overlay.style.left,
                originalWidth: overlay.style.width,
                active: false,
                sourceJson: readStateToken(),
            };
            me.preventDefault();
            return;
        }
        moveState = {
            clipIdx,
            el: overlay,
            startX: me.clientX,
            startStart: clip.retake.startSeconds,
            length: clip.retake.lengthSeconds,
            clipDuration,
            originalLeft: overlay.style.left,
            active: false,
            sourceJson: readStateToken(),
        };
        me.preventDefault();
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
                    resizeState.startStart + resizeState.startLength + deltaSec,
                    resizeState.startStart + RETAKE_MIN_DURATION,
                    clipDur,
                );
                resizeState.el.style.width = `${widthPct(end - resizeState.startStart, clipDur)}%`;
            } else {
                const end = resizeState.startStart + resizeState.startLength;
                const start = clamp(
                    resizeState.startStart + deltaSec,
                    0,
                    end - RETAKE_MIN_DURATION,
                );
                resizeState.el.style.left = `${leftPct(start, clipDur)}%`;
                resizeState.el.style.width = `${widthPct(end - start, clipDur)}%`;
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
            const clipDur = moveState.clipDuration;
            const length = Math.min(moveState.length, clipDur);
            const maxStart = Math.max(0, clipDur - length);
            const start = clamp(moveState.startStart + dx / pps, 0, maxStart);
            moveState.el.style.left = `${leftPct(start, clipDur)}%`;
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
        const overlay = event.target.closest(RETAKE_SELECTOR);
        if (!(overlay instanceof HTMLElement)) {
            return;
        }
        // Selecting/deleting the retake must not bubble to the region's clip-select handler.
        event.stopImmediatePropagation();
        const clipIdx = parseIntAttr(overlay, "data-clip-idx");
        if (clipIdx === null) {
            return;
        }
        const clip = getClips()[clipIdx];
        if (!clip?.retake) {
            return;
        }
        if ((event as MouseEvent).shiftKey) {
            deleteRetake(clipIdx);
            return;
        }
        setSelection({ kind: "retake", clipIdx });
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (ke.key !== "Enter" && ke.key !== " " && ke.key !== "Spacebar") {
            return;
        }
        if (!(ke.target instanceof Element)) {
            return;
        }
        const overlay = ke.target.closest(RETAKE_SELECTOR);
        if (!(overlay instanceof HTMLElement)) {
            return;
        }
        ke.preventDefault();
        ke.stopImmediatePropagation();
        const clipIdx = parseIntAttr(overlay, "data-clip-idx");
        if (clipIdx === null) {
            return;
        }
        if (!getClips()[clipIdx]?.retake) {
            return;
        }
        setSelection({ kind: "retake", clipIdx });
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
        } else {
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
        body.addEventListener("keydown", onBodyKeyDown);
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
            boundBody.removeEventListener("keydown", onBodyKeyDown);
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
        boundBody = null;
    };

    return { attach, dispose };
};
