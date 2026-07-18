import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    clamp,
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
import type { AudioSegment, Clip } from "./types";
import { setSelection } from "./uiState";

const SEG_SELECTOR = ".vst-audio-seg[data-clip-idx][data-seg-idx]";
const SEG_EDGE_SELECTOR = "[data-vst-audio-seg-edge]";
const LANE_SELECTOR = ".vst-audio-seg-lane[data-vst-audio-seg-add]";

const DRAG_THRESHOLD_PX = 4;
const DRAGGING_CLASS = "vst-audio-seg-dragging";
const GHOST_CLASS = "vst-audio-seg-ghost";

interface MoveState {
    clipIdx: number;
    segIdx: number;
    el: HTMLElement;
    startStart: number;
    length: number;
    clipDuration: number;
    // Free-interval walls around the segment at press: like a relay window,
    // a moved segment can never cross a neighbouring segment.
    boundLo: number;
    boundHi: number;
    originalLeft: string;
    sourceJson: string;
}

interface ResizeState {
    clipIdx: number;
    segIdx: number;
    edge: "left" | "right";
    el: HTMLElement;
    startStart: number;
    startLength: number;
    startTrim: number;
    clipDuration: number;
    // Free-interval walls around the segment at press (neighbour edges).
    wallLo: number;
    wallHi: number;
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

export interface TimelineAudioSegmentTrack {
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

const segmentAt = (
    clipIdx: number,
    segIdx: number,
): { clip: Clip; segment: AudioSegment } | null => {
    const clip = getClips()[clipIdx];
    const segment = clip?.audioSegments?.[segIdx];
    return clip && segment ? { clip, segment } : null;
};

// Occupied spans of every OTHER segment — audio segments obey the same
// non-overlap discipline as relay prompt windows (see timelinePromptTrack).
const otherSpans = (
    segments: AudioSegment[] | undefined,
    excludeIdx: number,
    clipDuration: number,
): Span[] =>
    (segments ?? [])
        .map((seg, k) => ({
            k,
            start: clamp(seg.startSeconds, 0, clipDuration),
            end: clamp(seg.startSeconds + seg.lengthSeconds, 0, clipDuration),
        }))
        .filter((s) => s.k !== excludeIdx && s.end > s.start)
        .sort((a, b) => a.start - b.start)
        .map((s) => ({ start: s.start, end: s.end }));

/**
 * NEIGHBOUR walls for a segment's start/end edges — the interval the segment
 * may occupy without overlapping another segment. Mirror of
 * promptWindowNeighborBounds; used by the dock's Start/Length clamps.
 */
export const audioSegmentNeighborBounds = (
    clip: Clip,
    segIdx: number,
): { startMin: number; endMax: number } | null => {
    const segment = clip.audioSegments?.[segIdx];
    if (!segment) {
        return null;
    }
    const clipDur = clipDurationOf(clip);
    const end = segment.startSeconds + segment.lengthSeconds;
    const spans = otherSpans(clip.audioSegments, segIdx, clipDur);
    const [lo] = freeIntervalAt(spans, clipDur, Math.max(0, end - 1e-3));
    const [, hi] = freeIntervalAt(spans, clipDur, segment.startSeconds);
    return { startMin: roundSeconds(lo), endMax: roundSeconds(hi) };
};

/**
 * The first free gap in the clip's segment lane that can host a new segment —
 * used by the dock's "+ add segment", which has no pressed time to anchor on.
 * Returns the gap start plus the (default-capped) length a new segment may
 * take there, or null when the lane is full.
 */
export const firstAudioSegmentGap = (
    clip: Clip,
): { start: number; length: number } | null => {
    const clipDur = clipDurationOf(clip);
    const spans = otherSpans(clip.audioSegments, -1, clipDur);
    let cursor = 0;
    const gapAt = (
        lo: number,
        hi: number,
    ): { start: number; length: number } | null =>
        hi - lo >= AUDIO_SEGMENT_MIN_LENGTH
            ? {
                  start: roundSeconds(lo),
                  length: roundSeconds(
                      Math.min(AUDIO_SEGMENT_DEFAULT_LENGTH, hi - lo),
                  ),
              }
            : null;
    for (const span of spans) {
        const gap = gapAt(cursor, span.start);
        if (gap) {
            return gap;
        }
        cursor = Math.max(cursor, span.end);
    }
    return gapAt(cursor, clipDur);
};

/**
 * Left-edge resize moves the start while the end stays fixed — exactly like a
 * relay window's begin edge, bounded only by the neighbour wall (`wallLo`).
 * The source trim FOLLOWS the edge (dragging right trims the head, dragging
 * left un-trims) but floors at 0: once fully un-trimmed, moving further left
 * simply starts the segment earlier. Right-edge resize only changes length,
 * clamped at the next neighbour (`wallHi`).
 */
const resizeLeft = (
    state: ResizeState,
    deltaSec: number,
    wallLo: number,
): { start: number; trim: number; length: number } => {
    const end = state.startStart + state.startLength;
    const start = clamp(
        state.startStart + deltaSec,
        Math.min(wallLo, end - AUDIO_SEGMENT_MIN_LENGTH),
        end - AUDIO_SEGMENT_MIN_LENGTH,
    );
    return {
        start,
        trim: Math.max(0, state.startTrim + (start - state.startStart)),
        length: end - start,
    };
};

const resizeRight = (
    state: ResizeState,
    deltaSec: number,
    wallHi: number,
): { length: number } => {
    const end = clamp(
        state.startStart + state.startLength + deltaSec,
        state.startStart + AUDIO_SEGMENT_MIN_LENGTH,
        wallHi,
    );
    return { length: end - state.startStart };
};

export const createTimelineAudioSegmentTrack =
    (): TimelineAudioSegmentTrack => {
        let boundBody: HTMLElement | null = null;
        let unregister: (() => void) | null = null;

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
            saveClips(clips, undefined, { origin: "audio-segment-track" });
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
            const maxStart = Math.max(state.boundLo, state.boundHi - length);
            const start = clamp(
                state.startStart + dxPx / pps,
                state.boundLo,
                maxStart,
            );
            segment.startSeconds = roundSeconds(start);
            segment.lengthSeconds = roundSeconds(
                Math.min(length, state.boundHi - segment.startSeconds),
            );
            saveClips(clips, undefined, { origin: "audio-segment-track" });
            // The dragged segment becomes the selection — after the save, so a
            // dock focus-restore can't re-point the selection elsewhere.
            setSelection({
                kind: "audio-segment",
                clipIdx: state.clipIdx,
                segIdx: state.segIdx,
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
            const segment = clips[state.clipIdx]?.audioSegments?.[state.segIdx];
            if (!segment) {
                return;
            }
            const deltaSec = dxPx / pps;
            if (state.edge === "right") {
                const next = resizeRight(state, deltaSec, state.wallHi);
                segment.startSeconds = roundSeconds(state.startStart);
                segment.lengthSeconds = roundSeconds(next.length);
            } else {
                const next = resizeLeft(state, deltaSec, state.wallLo);
                segment.startSeconds = roundSeconds(next.start);
                segment.trimStartSeconds = roundSeconds(Math.max(0, next.trim));
                segment.lengthSeconds = roundSeconds(next.length);
            }
            saveClips(clips, undefined, { origin: "audio-segment-track" });
            // See commitMove: select the resized segment, after the save.
            setSelection({
                kind: "audio-segment",
                clipIdx: state.clipIdx,
                segIdx: state.segIdx,
            });
        };

        // Create a segment from a lane press — same rules as a relay window:
        // it fills at most the free interval around the pressed time, never
        // overlapping an existing segment. `endSec === null` = plain tap →
        // default length; otherwise the drag span [startSec, endSec].
        const commitCreate = (
            state: CreateState,
            endSec: number | null,
        ): void => {
            if (isStale(state.sourceJson)) {
                return;
            }
            const clips = getClips();
            const clip = clips[state.clipIdx];
            if (!clip) {
                return;
            }
            const clipDur = clipDurationOf(clip);
            const spans = otherSpans(clip.audioSegments, -1, clipDur);
            const [lo, hi] = freeIntervalAt(spans, clipDur, state.startSec);
            const gap = hi - lo;
            if (gap < AUDIO_SEGMENT_MIN_LENGTH) {
                return;
            }
            let start: number;
            let length: number;
            if (endSec === null) {
                length = Math.min(AUDIO_SEGMENT_DEFAULT_LENGTH, gap);
                start = clamp(state.startSec, lo, hi - length);
            } else {
                const a = clamp(Math.min(state.startSec, endSec), lo, hi);
                const b = clamp(Math.max(state.startSec, endSec), lo, hi);
                start = a;
                length = Math.max(AUDIO_SEGMENT_MIN_LENGTH, b - a);
                if (start + length > hi) {
                    length = hi - start;
                }
            }
            if (length < AUDIO_SEGMENT_MIN_LENGTH) {
                return;
            }
            const segment: AudioSegment = {
                source: null,
                startSeconds: roundSeconds(start),
                trimStartSeconds: 0,
                lengthSeconds: roundSeconds(length),
            };
            const segments = [...(clip.audioSegments ?? []), segment];
            segments.sort((x, y) => x.startSeconds - y.startSeconds);
            clip.audioSegments = segments;
            saveClips(clips, undefined, { origin: "audio-segment-track" });
            // Open the new segment in the dock ready to edit.
            const newIdx = segments.indexOf(segment);
            if (newIdx >= 0) {
                setSelection({
                    kind: "audio-segment",
                    clipIdx: state.clipIdx,
                    segIdx: newIdx,
                });
            }
        };

        const laneTimeAt = (
            state: CreateState,
            clientX: number,
            pps: number,
        ): number =>
            clamp((clientX - state.laneLeft) / pps, 0, state.clipDuration);

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
                // A plain lane tap creates a default-length segment at the
                // pressed time, so the concluding click is always consumed.
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
                        laneTimeAt(
                            state,
                            ctx.event.clientX,
                            livePxPerSecond(body),
                        ),
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
                        const next = resizeRight(state, deltaSec, state.wallHi);
                        state.el.style.width = `${widthPct(next.length, clipDur)}%`;
                    } else {
                        const next = resizeLeft(state, deltaSec, state.wallLo);
                        state.el.style.left = `${leftPct(next.start, clipDur)}%`;
                        state.el.style.width = `${widthPct(next.length, clipDur)}%`;
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
                    const maxStart = Math.max(
                        state.boundLo,
                        state.boundHi - length,
                    );
                    const start = clamp(
                        state.startStart + ctx.dx / pps,
                        state.boundLo,
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
            const overlay = me.target.closest(SEG_SELECTOR);
            if (overlay instanceof HTMLElement) {
                if (me.shiftKey) {
                    // The segment owns this press (keeps the audio-clip select
                    // / region drag from firing); the shift-CLICK delete stays
                    // in onBodyClick.
                    me.preventDefault();
                    return claimOnly();
                }
                const clipIdx = parseIntAttr(overlay, "data-clip-idx");
                const segIdx = parseIntAttr(overlay, "data-seg-idx");
                if (clipIdx === null || segIdx === null) {
                    return null;
                }
                const found = segmentAt(clipIdx, segIdx);
                if (!found) {
                    return null;
                }
                const clipDuration = clipDurationOf(found.clip);
                const spans = otherSpans(
                    found.clip.audioSegments,
                    segIdx,
                    clipDuration,
                );
                const segEnd =
                    found.segment.startSeconds + found.segment.lengthSeconds;
                const [wallLo] = freeIntervalAt(
                    spans,
                    clipDuration,
                    Math.max(0, segEnd - 1e-3),
                );
                const [, wallHi] = freeIntervalAt(
                    spans,
                    clipDuration,
                    found.segment.startSeconds,
                );
                const edgeEl = me.target.closest(SEG_EDGE_SELECTOR);
                me.preventDefault();
                if (edgeEl) {
                    return resizeSession(body, {
                        clipIdx,
                        segIdx,
                        edge:
                            edgeEl.getAttribute("data-vst-audio-seg-edge") ===
                            "left"
                                ? "left"
                                : "right",
                        el: overlay,
                        startStart: found.segment.startSeconds,
                        startLength: found.segment.lengthSeconds,
                        startTrim: found.segment.trimStartSeconds,
                        clipDuration,
                        wallLo,
                        wallHi,
                        originalLeft: overlay.style.left,
                        originalWidth: overlay.style.width,
                        sourceJson: readStateToken(),
                    });
                }
                return moveSession(body, {
                    clipIdx,
                    segIdx,
                    el: overlay,
                    startStart: found.segment.startSeconds,
                    length: found.segment.lengthSeconds,
                    clipDuration,
                    boundLo: wallLo,
                    boundHi: wallHi,
                    originalLeft: overlay.style.left,
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

        const attach = (body: HTMLElement, router: GestureRouter): void => {
            if (boundBody === body) {
                return;
            }
            dispose();
            boundBody = body;
            body.addEventListener("click", onBodyClick);
            body.addEventListener("keydown", onBodyKeyDown);
            unregister = router.register({
                id: "audio-segment",
                priority: 40,
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
