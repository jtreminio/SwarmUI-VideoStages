import { AUDIO_SEGMENT_MIN_LENGTH, clamp } from "./constants";
import { getClips, saveClips } from "./persistence";
import { readStateToken } from "./swarmInputs";
import { livePxPerSecond } from "./timelineLinking";
import type { AudioSegment, Clip } from "./types";
import { setSelection } from "./uiState";

const SEG_SELECTOR = ".vst-audio-seg[data-clip-idx][data-seg-idx]";
const SEG_EDGE_SELECTOR = "[data-vst-audio-seg-edge]";

const DRAG_THRESHOLD_PX = 4;
const DRAGGING_CLASS = "vst-audio-seg-dragging";

interface MoveState {
    clipIdx: number;
    segIdx: number;
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
    segIdx: number;
    edge: "left" | "right";
    el: HTMLElement;
    startX: number;
    startStart: number;
    startLength: number;
    startTrim: number;
    clipDuration: number;
    originalLeft: string;
    originalWidth: string;
    active: boolean;
    sourceJson: string;
}

export interface TimelineAudioSegmentTrack {
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

const segmentAt = (
    clipIdx: number,
    segIdx: number,
): { clip: Clip; segment: AudioSegment } | null => {
    const clip = getClips()[clipIdx];
    const segment = clip?.audioSegments?.[segIdx];
    return clip && segment ? { clip, segment } : null;
};

/**
 * Left-edge resize trims the head: the visible left boundary and the source
 * playback offset move together while the end stays fixed. Right-edge resize
 * only changes length. `trimStart` never goes below 0, `length` never below
 * the minimum.
 */
const resizeLeft = (
    state: ResizeState,
    deltaSec: number,
): { start: number; trim: number; length: number } => {
    const end = state.startStart + state.startLength;
    const minStart = Math.max(0, state.startStart - state.startTrim);
    const start = clamp(
        state.startStart + deltaSec,
        minStart,
        end - AUDIO_SEGMENT_MIN_LENGTH,
    );
    return {
        start,
        trim: state.startTrim + (start - state.startStart),
        length: end - start,
    };
};

const resizeRight = (
    state: ResizeState,
    deltaSec: number,
    clipDur: number,
): { length: number } => {
    const end = clamp(
        state.startStart + state.startLength + deltaSec,
        state.startStart + AUDIO_SEGMENT_MIN_LENGTH,
        clipDur,
    );
    return { length: end - state.startStart };
};

export const createTimelineAudioSegmentTrack =
    (): TimelineAudioSegmentTrack => {
        let moveState: MoveState | null = null;
        let resizeState: ResizeState | null = null;
        let suppressClick = false;
        let boundBody: HTMLElement | null = null;

        const isStale = (sourceJson: string): boolean =>
            readStateToken() !== sourceJson;

        const deleteSegment = (clipIdx: number, segIdx: number): void => {
            const clips = getClips();
            const clip = clips[clipIdx];
            if (!clip?.audioSegments?.[segIdx]) {
                return;
            }
            clip.audioSegments = clip.audioSegments.filter(
                (_, i) => i !== segIdx,
            );
            saveClips(clips);
        };

        const commitMove = (
            state: MoveState,
            dxPx: number,
            pps: number,
        ): void => {
            if (isStale(state.sourceJson)) {
                return;
            }
            const clips = getClips();
            const segment = clips[state.clipIdx]?.audioSegments?.[state.segIdx];
            if (!segment) {
                return;
            }
            const clipDur = clipDurationOf(clips[state.clipIdx]);
            const length = Math.min(state.length, clipDur);
            const maxStart = Math.max(0, clipDur - length);
            const start = clamp(state.startStart + dxPx / pps, 0, maxStart);
            segment.startSeconds = roundSeconds(start);
            segment.lengthSeconds = roundSeconds(
                Math.min(length, clipDur - segment.startSeconds),
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
            const segment = clips[state.clipIdx]?.audioSegments?.[state.segIdx];
            if (!segment) {
                return;
            }
            const clipDur = clipDurationOf(clips[state.clipIdx]);
            const deltaSec = dxPx / pps;
            if (state.edge === "right") {
                const next = resizeRight(state, deltaSec, clipDur);
                segment.startSeconds = roundSeconds(state.startStart);
                segment.lengthSeconds = roundSeconds(next.length);
            } else {
                const next = resizeLeft(state, deltaSec);
                segment.startSeconds = roundSeconds(next.start);
                segment.trimStartSeconds = roundSeconds(Math.max(0, next.trim));
                segment.lengthSeconds = roundSeconds(next.length);
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
            const overlay = me.target.closest(SEG_SELECTOR);
            if (!(overlay instanceof HTMLElement)) {
                return;
            }
            // The segment owns this gesture; keep the audio-clip select/region drag
            // from also firing.
            me.stopImmediatePropagation();
            if (me.shiftKey) {
                me.preventDefault();
                return;
            }
            const clipIdx = parseIntAttr(overlay, "data-clip-idx");
            const segIdx = parseIntAttr(overlay, "data-seg-idx");
            if (clipIdx === null || segIdx === null) {
                return;
            }
            const found = segmentAt(clipIdx, segIdx);
            if (!found) {
                return;
            }
            const clipDuration = clipDurationOf(found.clip);
            const edgeEl = me.target.closest(SEG_EDGE_SELECTOR);
            if (edgeEl) {
                resizeState = {
                    clipIdx,
                    segIdx,
                    edge:
                        edgeEl.getAttribute("data-vst-audio-seg-edge") ===
                        "left"
                            ? "left"
                            : "right",
                    el: overlay,
                    startX: me.clientX,
                    startStart: found.segment.startSeconds,
                    startLength: found.segment.lengthSeconds,
                    startTrim: found.segment.trimStartSeconds,
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
                segIdx,
                el: overlay,
                startX: me.clientX,
                startStart: found.segment.startSeconds,
                length: found.segment.lengthSeconds,
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
                    const next = resizeRight(resizeState, deltaSec, clipDur);
                    resizeState.el.style.width = `${widthPct(next.length, clipDur)}%`;
                } else {
                    const next = resizeLeft(resizeState, deltaSec);
                    resizeState.el.style.left = `${leftPct(next.start, clipDur)}%`;
                    resizeState.el.style.width = `${widthPct(next.length, clipDur)}%`;
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
                const start = clamp(
                    moveState.startStart + dx / pps,
                    0,
                    maxStart,
                );
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
            const overlay = event.target.closest(SEG_SELECTOR);
            if (!(overlay instanceof HTMLElement)) {
                return;
            }
            // Selecting/deleting the segment must not bubble to the audio clip's
            // select handler.
            event.stopImmediatePropagation();
            const clipIdx = parseIntAttr(overlay, "data-clip-idx");
            const segIdx = parseIntAttr(overlay, "data-seg-idx");
            if (clipIdx === null || segIdx === null) {
                return;
            }
            if (!segmentAt(clipIdx, segIdx)) {
                return;
            }
            if ((event as MouseEvent).shiftKey) {
                deleteSegment(clipIdx, segIdx);
                return;
            }
            setSelection({ kind: "audio-segment", clipIdx, segIdx });
        };

        const onBodyKeyDown = (event: Event): void => {
            const ke = event as KeyboardEvent;
            if (ke.key !== "Enter" && ke.key !== " " && ke.key !== "Spacebar") {
                return;
            }
            if (!(ke.target instanceof Element)) {
                return;
            }
            const overlay = ke.target.closest(SEG_SELECTOR);
            if (!(overlay instanceof HTMLElement)) {
                return;
            }
            ke.preventDefault();
            ke.stopImmediatePropagation();
            const clipIdx = parseIntAttr(overlay, "data-clip-idx");
            const segIdx = parseIntAttr(overlay, "data-seg-idx");
            if (clipIdx === null || segIdx === null) {
                return;
            }
            if (!segmentAt(clipIdx, segIdx)) {
                return;
            }
            setSelection({ kind: "audio-segment", clipIdx, segIdx });
        };

        const onDocKeyDown = (
            body: HTMLElement,
            event: KeyboardEvent,
        ): void => {
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
