/**
 * Shared gesture lifecycle for every "span on a lane" track (retake, relay
 * prompt windows, timeline-wide audio spans). Each track was originally a
 * near-verbatim copy of the same move / edge-resize / drag-to-create-with-
 * ghost / tap-create / shift-click-delete machinery; this factory owns that
 * skeleton once, and the tracks supply a config of selectors plus PURE
 * geometry functions.
 *
 * The same `moveTargetStart` / `resizeTarget` functions drive BOTH the live
 * preview and the commit, so a preview can never show a position the commit
 * would reject (the old prompt-track preview/commit clamp drift).
 *
 * Press-time state: previews compute against the LANE SNAPSHOT taken at press
 * (no model reads per mousemove); commits re-read the live model through the
 * scope and are guarded by the carrier stale-token, so both see identical
 * values.
 *
 * The owning entity is a `scope`, not a clip: clip-lane tracks use
 * `clipWindowTrackScope`, and the audio track owns whole `AudioTrack`s keyed by
 * `data-track-idx` against the total timeline duration.
 */

import { clamp } from "./constants";
import {
    claimOnly,
    type GestureRouter,
    type GestureSession,
} from "./gestureRouter";
import { getClips, saveClips } from "./persistence/repository";
import { activateSelection, setSelection } from "./selection";
import type { UpdateOrigin } from "./store";
import { getTimelineAuthoringSettings } from "./timelineAuthoringSettings";
import { SNAP_THRESHOLD_PX, snapMovedStart, snapPoint } from "./timelineSnap";
import {
    clipDurationOf,
    currentRevision,
    isActivateKey,
    isStaleRevision,
    livePxPerSecond,
    parseIntAttr,
    spanGeometry,
} from "./trackDomUtils";
import type { Clip, TimelineSelection } from "./types";

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

/**
 * The shared "tap places a default-length span vs a drag sizes it" create math
 * for the window tracks. Everything is clamped into the free interval [lo, hi]
 * with a `minLen` floor; returns null when the interval can't hold a
 * minimum-length span.
 */
export const createDefaultOrDraggedSpan = (
    startSec: number,
    endSec: number | null,
    lo: number,
    hi: number,
    minLen: number,
    defaultLen: number,
): SpanGeom | null => {
    const gap = hi - lo;
    if (gap < minLen) {
        return null;
    }
    let start: number;
    let length: number;
    if (endSec === null) {
        length = Math.min(defaultLen, gap);
        start = clamp(startSec, lo, hi - length);
    } else {
        const a = clamp(Math.min(startSec, endSec), lo, hi);
        const b = clamp(Math.max(startSec, endSec), lo, hi);
        start = a;
        length = Math.max(minLen, b - a);
        if (start + length > hi) {
            length = hi - start;
        }
    }
    return length < minLen ? null : { start, length };
};

/**
 * The shared min-length edge-resize clamp. Right edge: keep the start, clamp
 * the end into [start+minLen, hi]. Left edge: keep the end, clamp the start
 * into [lo, end-minLen]. `lo`/`hi` are the surrounding walls (clip bounds or
 * free-interval walls); a caller whose trim follows the edge passes a `lo`
 * below 0.
 */
export const resizeSpanEdge = (
    edge: "left" | "right",
    press: PressSpan,
    deltaSec: number,
    minLen: number,
    lo: number,
    hi: number,
): SpanGeom => {
    if (edge === "right") {
        const end = clamp(
            press.start + press.length + deltaSec,
            press.start + minLen,
            hi,
        );
        return { start: press.start, length: end - press.start };
    }
    const end = press.start + press.length;
    const start = clamp(press.start + deltaSec, lo, end - minLen);
    return { start, length: end - start };
};

/** The entity a lane's spans belong to, plus that lane's own duration. */
export interface WindowTrackLane<TOwner> {
    owner: TOwner;
    ownerIdx: number;
    duration: number;
}

/**
 * A lane press targeted by a create gesture. `owner` is null when the gesture
 * would materialise it (a new audio track).
 */
export interface WindowTrackCreateLane<TOwner> {
    owner: TOwner | null;
    ownerIdx: number;
    duration: number;
}

/** A lane inside a write transaction; the scope persists what `mutate` changes. */
export interface WindowTrackTxn<TOwner> extends WindowTrackLane<TOwner> {
    /**
     * Drop the owning entity itself (the audio track behind its last span);
     * returns how many owners remain. Absent for scopes whose owners outlive
     * their spans.
     */
    removeOwner?(): number;
}

/**
 * Resolves which entity a lane's spans belong to and owns the read/write path
 * to it. This is the whole of a track's domain scope: everything else in the
 * config is DOM selectors and pure geometry.
 */
export interface WindowTrackScope<TOwner> {
    /** Live lane for press-time previews and guards. */
    read(ownerIdx: number): WindowTrackLane<TOwner> | null;
    /** Lane press → the entity a create targets, plus the lane duration. */
    resolveLane(lane: HTMLElement): WindowTrackCreateLane<TOwner> | null;
    /**
     * Runs `mutate` against a writable copy and persists it when `mutate`
     * returns true. With `create`, the entity is materialised when missing.
     */
    write(
        ownerIdx: number,
        create: boolean,
        mutate: (txn: WindowTrackTxn<TOwner>) => boolean,
    ): boolean;
}

/** Snap candidates for a lane: `primary` wins over `fallback` when both are in range. */
export interface WindowTrackSnapTargets {
    primary: number[];
    fallback: number[];
}

export interface WindowTrackConfig<TOwner = Clip> {
    routeId: string;
    priority: number;
    scope: WindowTrackScope<TOwner>;
    /** The draggable span element; carries the owner-index attribute. */
    spanSelector: string;
    /** Owner-index attribute on the span and lane; defaults to data-clip-idx. */
    ownerIdxAttr?: string;
    /** Per-item index attribute on the span; null = one span per owner (index 0). */
    itemIdxAttr: string | null;
    edgeSelector: string;
    /** Attribute on the grip holding "left" (anything else = right). */
    edgeAttr: string;
    /** Empty-lane element that creates a new span on press. */
    laneSelector: string;
    /**
     * Button outside the lanes (the audio track head's "+") that creates a
     * default-length span at the start of the timeline on click or Enter.
     */
    createButtonSelector?: string;
    draggingClass: string;
    ghostClass: string;
    /** Preview/ghost positioning: % of the lane, or px at the live pps. */
    unit: "pct" | "px";
    /** Install an Enter/Space select handler on the span. */
    keyboardSelect: boolean;
    /**
     * stopImmediatePropagation on span click/keydown — for spans nested inside
     * another clickable surface (retake in the clip region, span on the
     * audio row).
     */
    isolateClicks: boolean;
    /**
     * Re-emit span activation even when it is already selected. Singleton
     * tracks use this to reopen/reveal a manually closed sidebar section.
     */
    revealOnActivate?: boolean;
    readSpan(lane: WindowTrackLane<TOwner>, itemIdx: number): PressSpan | null;
    /** Existing unsupported persisted spans remain selectable/removable only. */
    canEdit?(lane: WindowTrackLane<TOwner>): boolean;
    /** Lane-press guard (retake: only when the clip has none yet). */
    canCreate?(lane: WindowTrackCreateLane<TOwner>): boolean;
    /**
     * Snap candidates for this lane. Defaults to the lane's own walls; the
     * audio track supplies adjacent-track edges and clip boundaries instead.
     */
    snapTargets?(ownerIdx: number, duration: number): WindowTrackSnapTargets;
    /** Pure clamped start for a move; drives both preview and commit. */
    moveTargetStart(
        lane: WindowTrackLane<TOwner>,
        itemIdx: number,
        press: PressSpan,
        desiredStart: number,
    ): number;
    writeMove(
        txn: WindowTrackTxn<TOwner>,
        itemIdx: number,
        press: PressSpan,
        start: number,
    ): void;
    /** Pure clamped geometry for an edge resize; drives preview and commit. */
    resizeTarget(
        lane: WindowTrackLane<TOwner>,
        itemIdx: number,
        edge: "left" | "right",
        press: PressSpan,
        deltaSec: number,
    ): SpanGeom;
    /**
     * Persist an edge resize. A track whose span carries a source trim (audio)
     * advances it here from `press.trim` and the committed geometry.
     */
    writeResize(
        txn: WindowTrackTxn<TOwner>,
        itemIdx: number,
        edge: "left" | "right",
        press: PressSpan,
        geom: SpanGeom,
    ): void;
    /**
     * Place a new span from a lane gesture (endSec null = plain tap). Mutates
     * the owner and returns the selection to adopt, or null to reject.
     */
    createSpan(
        txn: WindowTrackTxn<TOwner>,
        startSec: number,
        endSec: number | null,
    ): TimelineSelection | null;
    /**
     * Shift-click delete; mutates and returns the selection to adopt (see
     * `selectionAfterRemoval`), or null when nothing was removed.
     */
    deleteItem(
        txn: WindowTrackTxn<TOwner>,
        itemIdx: number,
    ): TimelineSelection | null;
    selectionFor(ownerIdx: number, itemIdx: number): TimelineSelection;
    /** Body-click fallthrough for presses outside any span (prompt's MAJOR row). */
    onClickFallthrough?(event: MouseEvent, target: Element): void;
}

export interface WindowTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

/**
 * The clip-lane scope: spans belong to a `Clip` resolved from `data-clip-idx`,
 * and the lane is the clip's own duration.
 */
export const clipWindowTrackScope = (
    origin: UpdateOrigin,
): WindowTrackScope<Clip> => {
    const laneFor = (
        clip: Clip | undefined,
        ownerIdx: number,
    ): WindowTrackLane<Clip> | null =>
        clip ? { owner: clip, ownerIdx, duration: clipDurationOf(clip) } : null;
    return {
        read: (ownerIdx) => laneFor(getClips()[ownerIdx], ownerIdx),
        resolveLane: (lane) => {
            const ownerIdx = parseIntAttr(lane, "data-clip-idx");
            return ownerIdx === null
                ? null
                : laneFor(getClips()[ownerIdx], ownerIdx);
        },
        write: (ownerIdx, _create, mutate) => {
            const clips = getClips();
            const lane = laneFor(clips[ownerIdx], ownerIdx);
            if (!lane || !mutate(lane)) {
                return false;
            }
            saveClips(clips, { origin });
            return true;
        },
    };
};

interface MoveState<TOwner> {
    lane: WindowTrackLane<TOwner>;
    itemIdx: number;
    el: HTMLElement;
    press: PressSpan;
    originalLeft: string;
    sourceRevision: number;
}

interface ResizeState<TOwner> extends MoveState<TOwner> {
    edge: "left" | "right";
    originalWidth: string;
}

interface CreateState {
    ownerIdx: number;
    duration: number;
    laneEl: HTMLElement;
    laneLeft: number;
    startSec: number;
    ghost: HTMLElement | null;
    sourceRevision: number;
}

export const createWindowTrack = <TOwner = Clip>(
    config: WindowTrackConfig<TOwner>,
): WindowTrack => {
    let boundBody: HTMLElement | null = null;
    let unregister: (() => void) | null = null;
    const ownerAttr = config.ownerIdxAttr ?? "data-clip-idx";

    const snapTargets = (
        ownerIdx: number,
        duration: number,
    ): WindowTrackSnapTargets =>
        config.snapTargets?.(ownerIdx, duration) ?? {
            primary: [],
            fallback: [0, duration],
        };

    const spanStyle = (
        start: number,
        length: number,
        laneDuration: number,
        pps: number,
    ): { left: string; width: string } => {
        const pct = config.unit === "pct";
        const geometry = spanGeometry(start, length, laneDuration, {
            unit: pct ? "percent" : "px",
            pxPerSecond: pps,
            minWidth: pct ? undefined : 2,
        });
        const suffix = pct ? "%" : "px";
        return {
            left: `${geometry.left}${suffix}`,
            width: `${geometry.width}${suffix}`,
        };
    };

    const moveTarget = (
        lane: WindowTrackLane<TOwner>,
        itemIdx: number,
        press: PressSpan,
        desiredStart: number,
        pps: number,
    ): number => {
        const raw = config.moveTargetStart(lane, itemIdx, press, desiredStart);
        if (!getTimelineAuthoringSettings().snap) {
            return raw;
        }
        const targets = snapTargets(lane.ownerIdx, lane.duration);
        const snapped = snapMovedStart(
            raw,
            press.length,
            targets.primary,
            targets.fallback,
            SNAP_THRESHOLD_PX / pps,
        );
        return config.moveTargetStart(lane, itemIdx, press, snapped);
    };

    const resizeTarget = (
        lane: WindowTrackLane<TOwner>,
        itemIdx: number,
        edge: "left" | "right",
        press: PressSpan,
        deltaSec: number,
        pps: number,
    ): SpanGeom => {
        const raw = config.resizeTarget(lane, itemIdx, edge, press, deltaSec);
        if (!getTimelineAuthoringSettings().snap) {
            return raw;
        }
        const targets = snapTargets(lane.ownerIdx, lane.duration);
        const rawEdge = edge === "left" ? raw.start : raw.start + raw.length;
        const snappedEdge = snapPoint(
            rawEdge,
            targets.primary,
            targets.fallback,
            SNAP_THRESHOLD_PX / pps,
        );
        if (snappedEdge === rawEdge) {
            return raw;
        }
        const pressEdge =
            edge === "left" ? press.start : press.start + press.length;
        return config.resizeTarget(
            lane,
            itemIdx,
            edge,
            press,
            snappedEdge - pressEdge,
        );
    };

    /** Re-reads the live owner, re-checks the edit guard, then persists. */
    const commitEdit = (
        state: MoveState<TOwner>,
        write: (txn: WindowTrackTxn<TOwner>) => void,
    ): void => {
        if (isStaleRevision(state.sourceRevision)) {
            return;
        }
        const saved = config.scope.write(state.lane.ownerIdx, false, (txn) => {
            if (
                (config.canEdit && !config.canEdit(txn)) ||
                !config.readSpan(txn, state.itemIdx)
            ) {
                return false;
            }
            write(txn);
            return true;
        });
        if (saved) {
            // The dragged span becomes the selection — after the save, so a
            // dock focus-restore can't re-point the selection elsewhere.
            setSelection(
                config.selectionFor(state.lane.ownerIdx, state.itemIdx),
            );
        }
    };

    const commitMove = (
        state: MoveState<TOwner>,
        dxPx: number,
        pps: number,
    ): void => {
        commitEdit(state, (txn) => {
            config.writeMove(
                txn,
                state.itemIdx,
                state.press,
                moveTarget(
                    txn,
                    state.itemIdx,
                    state.press,
                    state.press.start + dxPx / pps,
                    pps,
                ),
            );
        });
    };

    const commitResize = (
        state: ResizeState<TOwner>,
        dxPx: number,
        pps: number,
    ): void => {
        commitEdit(state, (txn) => {
            config.writeResize(
                txn,
                state.itemIdx,
                state.edge,
                state.press,
                resizeTarget(
                    txn,
                    state.itemIdx,
                    state.edge,
                    state.press,
                    dxPx / pps,
                    pps,
                ),
            );
        });
    };

    const commitCreate = (state: CreateState, endSec: number | null): void => {
        if (isStaleRevision(state.sourceRevision)) {
            return;
        }
        const created: { selection: TimelineSelection | null } = {
            selection: null,
        };
        const saved = config.scope.write(state.ownerIdx, true, (txn) => {
            if (config.canCreate && !config.canCreate(txn)) {
                return false;
            }
            created.selection = config.createSpan(txn, state.startSec, endSec);
            return created.selection !== null;
        });
        if (saved && created.selection) {
            // Open the new span in the dock — after the save, so the rebuilt
            // panel already contains its row.
            setSelection(created.selection);
        }
    };

    const timeAtX = (
        lane: { ownerIdx: number; duration: number },
        laneLeft: number,
        clientX: number,
        pps: number,
    ): number => {
        const raw = clamp((clientX - laneLeft) / pps, 0, lane.duration);
        if (!getTimelineAuthoringSettings().snap) {
            return raw;
        }
        const targets = snapTargets(lane.ownerIdx, lane.duration);
        return snapPoint(
            raw,
            targets.primary,
            targets.fallback,
            SNAP_THRESHOLD_PX / pps,
        );
    };

    const laneTimeAt = (
        state: CreateState,
        clientX: number,
        pps: number,
    ): number => timeAtX(state, state.laneLeft, clientX, pps);

    const moveSession = (
        body: HTMLElement,
        state: MoveState<TOwner>,
    ): GestureSession => {
        const restore = (): void => {
            state.el.style.left = state.originalLeft;
        };
        return {
            threshold: DRAG_THRESHOLD_PX,
            onMove: (ctx) => {
                body.classList.add(config.draggingClass);
                const pps = livePxPerSecond(body);
                const start = moveTarget(
                    state.lane,
                    state.itemIdx,
                    state.press,
                    state.press.start + ctx.dx / pps,
                    pps,
                );
                state.el.style.left = spanStyle(
                    start,
                    state.press.length,
                    state.lane.duration,
                    pps,
                ).left;
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
        state: ResizeState<TOwner>,
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
                const geom = resizeTarget(
                    state.lane,
                    state.itemIdx,
                    state.edge,
                    state.press,
                    ctx.dx / pps,
                    pps,
                );
                const style = spanStyle(
                    geom.start,
                    geom.length,
                    state.lane.duration,
                    pps,
                );
                if (state.edge === "left") {
                    state.el.style.left = style.left;
                }
                state.el.style.width = style.width;
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
                    state.laneEl.appendChild(ghost);
                    state.ghost = ghost;
                }
                const ghostStyle = spanStyle(a, b - a, state.duration, pps);
                state.ghost.style.left = ghostStyle.left;
                state.ghost.style.width = ghostStyle.width;
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

    /** Handles a press on the out-of-lane create button; false when it wasn't one. */
    const createFromButton = (target: Element): boolean => {
        const button = config.createButtonSelector
            ? target.closest(config.createButtonSelector)
            : null;
        if (!(button instanceof HTMLElement)) {
            return false;
        }
        const lane = config.scope.resolveLane(button);
        if (lane && !(config.canCreate && !config.canCreate(lane))) {
            commitCreate(
                {
                    ownerIdx: lane.ownerIdx,
                    duration: lane.duration,
                    laneEl: button,
                    laneLeft: 0,
                    startSec: 0,
                    ghost: null,
                    sourceRevision: currentRevision(),
                },
                null,
            );
        }
        return true;
    };

    const itemIdxOf = (span: Element): number | null =>
        config.itemIdxAttr ? parseIntAttr(span, config.itemIdxAttr) : 0;

    /** The live lane + press geometry behind a span element, or null. */
    const spanAt = (
        span: Element,
    ): {
        lane: WindowTrackLane<TOwner>;
        itemIdx: number;
        press: PressSpan;
    } | null => {
        const ownerIdx = parseIntAttr(span, ownerAttr);
        const itemIdx = itemIdxOf(span);
        if (ownerIdx === null || itemIdx === null) {
            return null;
        }
        const lane = config.scope.read(ownerIdx);
        const press = lane ? config.readSpan(lane, itemIdx) : null;
        return lane && press ? { lane, itemIdx, press } : null;
    };

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
            const found = spanAt(span);
            if (!found) {
                return null;
            }
            if (config.canEdit && !config.canEdit(found.lane)) {
                me.preventDefault();
                return claimOnly();
            }
            const base: MoveState<TOwner> = {
                lane: found.lane,
                itemIdx: found.itemIdx,
                el: span,
                press: found.press,
                originalLeft: span.style.left,
                sourceRevision: currentRevision(),
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
        const laneEl = me.target.closest(config.laneSelector);
        if (laneEl instanceof HTMLElement) {
            const lane = config.scope.resolveLane(laneEl);
            if (!lane || (config.canCreate && !config.canCreate(lane))) {
                return null;
            }
            const rect = laneEl.getBoundingClientRect();
            me.preventDefault();
            return createSession(body, {
                ownerIdx: lane.ownerIdx,
                duration: lane.duration,
                laneEl,
                laneLeft: rect.left,
                startSec: timeAtX(
                    lane,
                    rect.left,
                    me.clientX,
                    livePxPerSecond(body),
                ),
                ghost: null,
                sourceRevision: currentRevision(),
            });
        }
        return null;
    };

    const activate = (selection: TimelineSelection): void => {
        if (config.revealOnActivate) {
            activateSelection(selection);
        } else {
            setSelection(selection);
        }
    };

    const onBodyClick = (event: Event): void => {
        if (!(event.target instanceof Element)) {
            return;
        }
        if (createFromButton(event.target)) {
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
        const found = spanAt(span);
        if (!found) {
            return;
        }
        if ((event as MouseEvent).shiftKey) {
            const removed: { selection: TimelineSelection | null } = {
                selection: null,
            };
            const saved = config.scope.write(
                found.lane.ownerIdx,
                false,
                (txn) => {
                    removed.selection = config.deleteItem(txn, found.itemIdx);
                    return removed.selection !== null;
                },
            );
            if (saved && removed.selection) {
                setSelection(removed.selection);
            }
            return;
        }
        activate(config.selectionFor(found.lane.ownerIdx, found.itemIdx));
    };

    const onBodyKeyDown = (event: Event): void => {
        const ke = event as KeyboardEvent;
        if (!isActivateKey(ke)) {
            return;
        }
        if (!(ke.target instanceof Element)) {
            return;
        }
        if (createFromButton(ke.target)) {
            ke.preventDefault();
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
        const found = spanAt(span);
        if (!found) {
            return;
        }
        activate(config.selectionFor(found.lane.ownerIdx, found.itemIdx));
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
