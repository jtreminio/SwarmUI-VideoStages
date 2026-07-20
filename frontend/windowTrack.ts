/**
 * Shared gesture lifecycle for the three "window on a clip lane" tracks
 * (retake, audio segments, relay prompt windows). Each track was originally a
 * near-verbatim copy of the same move / edge-resize / drag-to-create-with-
 * ghost / tap-create / shift-click-delete machinery; this factory owns that
 * skeleton once, and the tracks supply a config of selectors plus PURE
 * geometry functions.
 *
 * The same `moveTargetStart` / `resizeTarget` functions drive BOTH the live
 * preview and the commit, so a preview can never show a position the commit
 * would reject (the old prompt-track preview/commit clamp drift).
 *
 * Press-time state: previews compute against the CLIP SNAPSHOT taken at
 * press (no model reads per mousemove); commits re-read the live model and
 * are guarded by the carrier stale-token, so both see identical values.
 */

import { clamp } from "./constants";
import {
    claimOnly,
    type GestureRouter,
    type GestureSession,
} from "./gestureRouter";
import { getClips, saveClips } from "./persistence";
import type { UpdateOrigin } from "./store";
import { readStateToken } from "./swarmInputs";
import { livePxPerSecond } from "./timelineLinking";
import {
    clipDurationOf,
    isActivateKey,
    isStaleToken,
    leftPct,
    parseIntAttr,
    widthPct,
} from "./trackDomUtils";
import type { Clip, TimelineSelection } from "./types";
import { setSelection } from "./uiState";

const DRAG_THRESHOLD_PX = 4;

/** A span's geometry (seconds) captured at press; trim is 0 for spans without one. */
export interface PressSpan {
    start: number;
    length: number;
    trim: number;
}

export interface SpanGeom {
    start: number;
    length: number;
}

export interface WindowTrackConfig {
    routeId: string;
    priority: number;
    origin: UpdateOrigin;
    /** The draggable span element; carries data-clip-idx. */
    spanSelector: string;
    /** Per-item index attribute on the span; null = one span per clip (index 0). */
    itemIdxAttr: string | null;
    /** Resize grip inside the span. */
    edgeSelector: string;
    /** Attribute on the grip holding "left" (anything else = right). */
    edgeAttr: string;
    /** Empty-lane element that creates a new span on press. */
    laneSelector: string;
    draggingClass: string;
    ghostClass: string;
    /** Preview/ghost positioning: % of the clip lane, or px at the live pps. */
    unit: "pct" | "px";
    /** Install an Enter/Space select handler on the span. */
    keyboardSelect: boolean;
    /**
     * stopImmediatePropagation on span click/keydown — for spans nested inside
     * another clickable surface (retake in the clip region, segment on the
     * audio row).
     */
    isolateClicks: boolean;
    readSpan(clip: Clip, itemIdx: number): PressSpan | null;
    /** Lane-press guard (retake: only when the clip has none yet). */
    canCreate?(clip: Clip): boolean;
    /** Pure clamped start for a move; drives both preview and commit. */
    moveTargetStart(
        clip: Clip,
        itemIdx: number,
        press: PressSpan,
        desiredStart: number,
    ): number;
    writeMove(
        clip: Clip,
        itemIdx: number,
        press: PressSpan,
        start: number,
    ): void;
    /** Pure clamped geometry for an edge resize; drives preview and commit. */
    resizeTarget(
        clip: Clip,
        itemIdx: number,
        edge: "left" | "right",
        press: PressSpan,
        deltaSec: number,
    ): SpanGeom;
    writeResize(
        clip: Clip,
        itemIdx: number,
        edge: "left" | "right",
        press: PressSpan,
        geom: SpanGeom,
    ): void;
    /**
     * Place a new span from a lane gesture (endSec null = plain tap). Mutates
     * the clip and returns the selection to adopt, or null to reject.
     */
    createSpan(
        clip: Clip,
        clipIdx: number,
        startSec: number,
        endSec: number | null,
    ): TimelineSelection | null;
    /** Shift-click delete; true when something was removed. */
    deleteItem(clip: Clip, itemIdx: number): boolean;
    selectionFor(clipIdx: number, itemIdx: number): TimelineSelection;
    /** Body-click fallthrough for presses outside any span (prompt's MAJOR row). */
    onClickFallthrough?(event: MouseEvent, target: Element): void;
}

export interface WindowTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

interface MoveState {
    clipIdx: number;
    itemIdx: number;
    el: HTMLElement;
    press: PressSpan;
    clipAtPress: Clip;
    clipDuration: number;
    originalLeft: string;
    sourceJson: string;
}

interface ResizeState extends MoveState {
    edge: "left" | "right";
    originalWidth: string;
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

export const createWindowTrack = (config: WindowTrackConfig): WindowTrack => {
    let boundBody: HTMLElement | null = null;
    let unregister: (() => void) | null = null;

    const leftStyle = (start: number, clipDur: number, pps: number): string =>
        config.unit === "pct"
            ? `${leftPct(start, clipDur)}%`
            : `${start * pps}px`;

    const widthStyle = (
        length: number,
        clipDur: number,
        pps: number,
    ): string =>
        config.unit === "pct"
            ? `${widthPct(length, clipDur)}%`
            : `${Math.max(2, length * pps)}px`;

    const commitMove = (state: MoveState, dxPx: number, pps: number): void => {
        if (isStaleToken(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        if (!clip || !config.readSpan(clip, state.itemIdx)) {
            return;
        }
        const start = config.moveTargetStart(
            clip,
            state.itemIdx,
            state.press,
            state.press.start + dxPx / pps,
        );
        config.writeMove(clip, state.itemIdx, state.press, start);
        saveClips(clips, { origin: config.origin });
        // The dragged span becomes the selection — after the save, so a dock
        // focus-restore can't re-point the selection elsewhere.
        setSelection(config.selectionFor(state.clipIdx, state.itemIdx));
    };

    const commitResize = (
        state: ResizeState,
        dxPx: number,
        pps: number,
    ): void => {
        if (isStaleToken(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        if (!clip || !config.readSpan(clip, state.itemIdx)) {
            return;
        }
        const geom = config.resizeTarget(
            clip,
            state.itemIdx,
            state.edge,
            state.press,
            dxPx / pps,
        );
        config.writeResize(clip, state.itemIdx, state.edge, state.press, geom);
        saveClips(clips, { origin: config.origin });
        // See commitMove: select the resized span, after the save.
        setSelection(config.selectionFor(state.clipIdx, state.itemIdx));
    };

    const commitCreate = (state: CreateState, endSec: number | null): void => {
        if (isStaleToken(state.sourceJson)) {
            return;
        }
        const clips = getClips();
        const clip = clips[state.clipIdx];
        if (!clip || (config.canCreate && !config.canCreate(clip))) {
            return;
        }
        const selection = config.createSpan(
            clip,
            state.clipIdx,
            state.startSec,
            endSec,
        );
        if (!selection) {
            return;
        }
        saveClips(clips, { origin: config.origin });
        // Open the new span in the dock — after the save, so the rebuilt
        // panel already contains its row.
        setSelection(selection);
    };

    const laneTimeAt = (
        state: CreateState,
        clientX: number,
        pps: number,
    ): number => clamp((clientX - state.laneLeft) / pps, 0, state.clipDuration);

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
                body.classList.add(config.draggingClass);
                const pps = livePxPerSecond(body);
                const start = config.moveTargetStart(
                    state.clipAtPress,
                    state.itemIdx,
                    state.press,
                    state.press.start + ctx.dx / pps,
                );
                state.el.style.left = leftStyle(start, state.clipDuration, pps);
            },
            onCommit: (ctx) => {
                body.classList.remove(config.draggingClass);
                commitMove(state, ctx.dx, livePxPerSecond(body));
            },
            onTap: restore,
            onCancel: () => {
                restore();
                body.classList.remove(config.draggingClass);
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
                body.classList.add(config.draggingClass);
                const pps = livePxPerSecond(body);
                const geom = config.resizeTarget(
                    state.clipAtPress,
                    state.itemIdx,
                    state.edge,
                    state.press,
                    ctx.dx / pps,
                );
                if (state.edge === "left") {
                    state.el.style.left = leftStyle(
                        geom.start,
                        state.clipDuration,
                        pps,
                    );
                }
                state.el.style.width = widthStyle(
                    geom.length,
                    state.clipDuration,
                    pps,
                );
            },
            onCommit: (ctx) => {
                body.classList.remove(config.draggingClass);
                commitResize(state, ctx.dx, livePxPerSecond(body));
            },
            onTap: restore,
            onCancel: () => {
                restore();
                body.classList.remove(config.draggingClass);
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
            // A plain lane tap creates a default-length span at the pressed
            // time, so the concluding click is always consumed.
            suppressTapClick: true,
            onMove: (ctx) => {
                body.classList.add(config.draggingClass);
                const pps = livePxPerSecond(body);
                const nowSec = laneTimeAt(state, ctx.event.clientX, pps);
                const a = Math.min(state.startSec, nowSec);
                const b = Math.max(state.startSec, nowSec);
                if (!state.ghost) {
                    const ghost = document.createElement("div");
                    ghost.className = config.ghostClass;
                    state.lane.appendChild(ghost);
                    state.ghost = ghost;
                }
                state.ghost.style.left = leftStyle(a, state.clipDuration, pps);
                state.ghost.style.width = widthStyle(
                    b - a,
                    state.clipDuration,
                    pps,
                );
            },
            onCommit: (ctx) => {
                body.classList.remove(config.draggingClass);
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
                body.classList.remove(config.draggingClass);
            },
        };
    };

    const itemIdxOf = (span: Element): number | null =>
        config.itemIdxAttr ? parseIntAttr(span, config.itemIdxAttr) : 0;

    const onPress = (
        me: MouseEvent,
        body: HTMLElement,
    ): GestureSession | null => {
        if (!(me.target instanceof Element)) {
            return null;
        }
        const span = me.target.closest(config.spanSelector);
        if (span instanceof HTMLElement) {
            if (me.shiftKey) {
                // The span owns this press (keeps lower-priority routes from
                // acting); the shift-CLICK delete stays in onBodyClick.
                me.preventDefault();
                return claimOnly();
            }
            const clipIdx = parseIntAttr(span, "data-clip-idx");
            const itemIdx = itemIdxOf(span);
            if (clipIdx === null || itemIdx === null) {
                return null;
            }
            const clip = getClips()[clipIdx];
            const press = clip ? config.readSpan(clip, itemIdx) : null;
            if (!clip || !press) {
                return null;
            }
            const base: MoveState = {
                clipIdx,
                itemIdx,
                el: span,
                press,
                clipAtPress: clip,
                clipDuration: clipDurationOf(clip),
                originalLeft: span.style.left,
                sourceJson: readStateToken(),
            };
            me.preventDefault();
            const edgeEl = me.target.closest(config.edgeSelector);
            if (edgeEl) {
                return resizeSession(body, {
                    ...base,
                    edge:
                        edgeEl.getAttribute(config.edgeAttr) === "left"
                            ? "left"
                            : "right",
                    originalWidth: span.style.width,
                });
            }
            return moveSession(body, base);
        }
        const lane = me.target.closest(config.laneSelector);
        if (lane instanceof HTMLElement) {
            const clipIdx = parseIntAttr(lane, "data-clip-idx");
            if (clipIdx === null) {
                return null;
            }
            const clip = getClips()[clipIdx];
            if (!clip || (config.canCreate && !config.canCreate(clip))) {
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
    };

    const onBodyClick = (event: Event): void => {
        if (!(event.target instanceof Element)) {
            return;
        }
        const span = event.target.closest(config.spanSelector);
        if (!(span instanceof HTMLElement)) {
            config.onClickFallthrough?.(event as MouseEvent, event.target);
            return;
        }
        if (config.isolateClicks) {
            // Selecting/deleting the span must not bubble to the surface it
            // sits on (clip region select, audio row select).
            event.stopImmediatePropagation();
        }
        const clipIdx = parseIntAttr(span, "data-clip-idx");
        const itemIdx = itemIdxOf(span);
        if (clipIdx === null || itemIdx === null) {
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip || !config.readSpan(clip, itemIdx)) {
            return;
        }
        if ((event as MouseEvent).shiftKey) {
            if (config.deleteItem(clip, itemIdx)) {
                saveClips(clips, { origin: config.origin });
            }
            return;
        }
        setSelection(config.selectionFor(clipIdx, itemIdx));
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (!isActivateKey(ke)) {
            return;
        }
        if (!(ke.target instanceof Element)) {
            return;
        }
        const span = ke.target.closest(config.spanSelector);
        if (!(span instanceof HTMLElement)) {
            return;
        }
        ke.preventDefault();
        if (config.isolateClicks) {
            ke.stopImmediatePropagation();
        }
        const clipIdx = parseIntAttr(span, "data-clip-idx");
        const itemIdx = itemIdxOf(span);
        if (clipIdx === null || itemIdx === null) {
            return;
        }
        const clip = getClips()[clipIdx];
        if (!clip || !config.readSpan(clip, itemIdx)) {
            return;
        }
        setSelection(config.selectionFor(clipIdx, itemIdx));
    };

    const attach = (body: HTMLElement, router: GestureRouter): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("click", onBodyClick);
        if (config.keyboardSelect) {
            body.addEventListener("keydown", onBodyKeyDown);
        }
        unregister = router.register({
            id: config.routeId,
            priority: config.priority,
            onPress: (me) => onPress(me, body),
        });
    };

    const dispose = (): void => {
        if (boundBody) {
            boundBody.removeEventListener("click", onBodyClick);
            boundBody.removeEventListener("keydown", onBodyKeyDown);
            boundBody = null;
        }
        unregister?.();
        unregister = null;
    };

    return { attach, dispose };
};
