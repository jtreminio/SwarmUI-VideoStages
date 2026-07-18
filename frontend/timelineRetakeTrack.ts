import {
    clamp,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
} from "./constants";
import {
    claimOnly,
    type GestureRouter,
    type GestureSession,
} from "./gestureRouter";
import { getClips, saveClips } from "./persistence";
import { readStateToken } from "./swarmInputs";
import { livePxPerSecond } from "./timelineLinking";
import type { Clip } from "./types";
import { setSelection } from "./uiState";

const RETAKE_SELECTOR = ".vst-retake[data-clip-idx]";
const RETAKE_EDGE_SELECTOR = "[data-vst-retake-edge]";
const LANE_SELECTOR = ".vst-retake-lane[data-vst-retake-add]";

const DRAG_THRESHOLD_PX = 4;
const DRAGGING_CLASS = "vst-retake-dragging";
const GHOST_CLASS = "vst-retake-ghost";

interface MoveState {
    clipIdx: number;
    el: HTMLElement;
    startStart: number;
    length: number;
    clipDuration: number;
    originalLeft: string;
    sourceJson: string;
}

interface ResizeState {
    clipIdx: number;
    edge: "left" | "right";
    el: HTMLElement;
    startStart: number;
    startLength: number;
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

export interface TimelineRetakeTrack {
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

const leftPct = (start: number, duration: number): number =>
    duration > 0 ? (clamp(start, 0, duration) / duration) * 100 : 0;

const widthPct = (length: number, duration: number): number =>
    duration > 0 ? (clamp(length, 0, duration) / duration) * 100 : 0;

export const createTimelineRetakeTrack = (): TimelineRetakeTrack => {
    let boundBody: HTMLElement | null = null;
    let unregister: (() => void) | null = null;

    const isStale = (sourceJson: string): boolean =>
        readStateToken() !== sourceJson;

    const deleteRetake = (clipIdx: number): void => {
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip?.retake) {
            return;
        }
        clip.retake = null;
        saveClips(clips, undefined, { origin: "retake-track" });
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
        saveClips(clips, undefined, { origin: "retake-track" });
        // The dragged retake becomes the selection — after the save, so a dock
        // focus-restore can't re-point the selection elsewhere.
        setSelection({ kind: "retake", clipIdx: state.clipIdx });
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
        saveClips(clips, undefined, { origin: "retake-track" });
        // See commitMove: select the resized retake, after the save.
        setSelection({ kind: "retake", clipIdx: state.clipIdx });
    };

    // Create a retake from a lane press — a clip holds at most ONE retake, so
    // the lane only creates when none exists (onPress guards). A plain tap
    // places a default-length window at the pressed time; a drag sizes it.
    const commitCreate = (state: CreateState, endSec: number | null): void => {
        if (isStale(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        if (!clip || clip.retake) {
            return;
        }
        const clipDur = clipDurationOf(clip);
        if (clipDur < RETAKE_MIN_DURATION) {
            return;
        }
        let start: number;
        let length: number;
        if (endSec === null) {
            length = Math.min(RETAKE_DEFAULT_DURATION, clipDur);
            start = clamp(state.startSec, 0, clipDur - length);
        } else {
            const a = clamp(Math.min(state.startSec, endSec), 0, clipDur);
            const b = clamp(Math.max(state.startSec, endSec), 0, clipDur);
            start = a;
            length = Math.max(RETAKE_MIN_DURATION, b - a);
            if (start + length > clipDur) {
                length = clipDur - start;
            }
        }
        if (length < RETAKE_MIN_DURATION) {
            return;
        }
        clip.retake = {
            startSeconds: roundSeconds(start),
            lengthSeconds: roundSeconds(length),
            strength: RETAKE_STRENGTH_DEFAULT,
        };
        saveClips(clips, undefined, { origin: "retake-track" });
        // Open the new retake in the dock (the clip panel's Retake section).
        setSelection({ kind: "retake", clipIdx: state.clipIdx });
    };

    const laneTimeAt = (
        state: CreateState,
        clientX: number,
        pps: number,
    ): number => clamp((clientX - state.laneLeft) / pps, 0, state.clipDuration);

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
            // A plain lane tap creates a default-length retake, so the
            // concluding click is always consumed.
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
                const dur = state.clipDuration;
                state.ghost.style.left = `${leftPct(a, dur)}%`;
                state.ghost.style.width = `${widthPct(b - a, dur)}%`;
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
                        state.startStart + state.startLength + deltaSec,
                        state.startStart + RETAKE_MIN_DURATION,
                        clipDur,
                    );
                    state.el.style.width = `${widthPct(end - state.startStart, clipDur)}%`;
                } else {
                    const end = state.startStart + state.startLength;
                    const start = clamp(
                        state.startStart + deltaSec,
                        0,
                        end - RETAKE_MIN_DURATION,
                    );
                    state.el.style.left = `${leftPct(start, clipDur)}%`;
                    state.el.style.width = `${widthPct(end - start, clipDur)}%`;
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
                const clipDur = state.clipDuration;
                const length = Math.min(state.length, clipDur);
                const maxStart = Math.max(0, clipDur - length);
                const start = clamp(
                    state.startStart + ctx.dx / pps,
                    0,
                    maxStart,
                );
                state.el.style.left = `${leftPct(start, clipDur)}%`;
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

    const onPress = (
        me: MouseEvent,
        body: HTMLElement,
    ): GestureSession | null => {
        if (!(me.target instanceof Element)) {
            return null;
        }
        const overlay = me.target.closest(RETAKE_SELECTOR);
        if (!(overlay instanceof HTMLElement)) {
            // Empty lane press → create (only when the clip has no retake yet).
            const lane = me.target.closest(LANE_SELECTOR);
            if (lane instanceof HTMLElement) {
                const clipIdx = parseIntAttr(lane, "data-clip-idx");
                if (clipIdx === null) {
                    return null;
                }
                const clip = getClips()[clipIdx];
                if (!clip || clip.retake) {
                    return null;
                }
                const rect = lane.getBoundingClientRect();
                const pps = livePxPerSecond(body);
                const clipDuration = clipDurationOf(clip);
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
        }
        if (me.shiftKey) {
            // The retake owns this press (keeps the region drag from starting);
            // the shift-CLICK delete stays in onBodyClick.
            me.preventDefault();
            return claimOnly();
        }
        const clipIdx = parseIntAttr(overlay, "data-clip-idx");
        if (clipIdx === null) {
            return null;
        }
        const clip = getClips()[clipIdx];
        if (!clip?.retake) {
            return null;
        }
        const clipDuration = clipDurationOf(clip);
        const edgeEl = me.target.closest(RETAKE_EDGE_SELECTOR);
        me.preventDefault();
        if (edgeEl) {
            return resizeSession(body, {
                clipIdx,
                edge:
                    edgeEl.getAttribute("data-vst-retake-edge") === "left"
                        ? "left"
                        : "right",
                el: overlay,
                startStart: clip.retake.startSeconds,
                startLength: clip.retake.lengthSeconds,
                clipDuration,
                originalLeft: overlay.style.left,
                originalWidth: overlay.style.width,
                sourceJson: readStateToken(),
            });
        }
        return moveSession(body, {
            clipIdx,
            el: overlay,
            startStart: clip.retake.startSeconds,
            length: clip.retake.lengthSeconds,
            clipDuration,
            originalLeft: overlay.style.left,
            sourceJson: readStateToken(),
        });
    };

    const onBodyClick = (event: Event): void => {
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

    const attach = (body: HTMLElement, router: GestureRouter): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        body.addEventListener("keydown", onBodyKeyDown);
        unregister = router.register({
            id: "retake",
            priority: 50,
            onPress,
        });
    };

    const dispose = (): void => {
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
            boundBody.removeEventListener("keydown", onBodyKeyDown);
        }
        unregister?.();
        unregister = null;
        boundBody = null;
    };

    return { attach, dispose };
};
