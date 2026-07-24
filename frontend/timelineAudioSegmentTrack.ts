import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    AUDIO_SEGMENT_VOLUME_DEFAULT,
    clamp,
} from "./constants";
import {
    claimOnly,
    type GestureRouter,
    type GestureSession,
} from "./gestureRouter";
import { createEntityId } from "./identity";
import { getState, saveState } from "./persistence";
import { setSelection } from "./selection";
import { readStateToken } from "./swarmInputs";
import { getTimelineAuthoringSettings } from "./timelineAuthoringSettings";
import {
    SNAP_THRESHOLD_PX,
    snapMovedStart,
    snapPoint,
    timelineClipEdges,
} from "./timelineSnap";
import {
    isActivateKey,
    isStaleToken,
    spanGeometry as laneSpanGeometry,
    livePxPerSecond,
    parseIntAttr,
} from "./trackDomUtils";
import type { AudioTrack, AudioTrackSpan, VideoStagesConfig } from "./types";
import { roundToTenth } from "./utils";
import { createDefaultOrDraggedSpan } from "./windowTrack";

export interface TimelineAudioSegmentTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

const DRAG_THRESHOLD_PX = 4;

const timelineDuration = (state: VideoStagesConfig): number =>
    state.clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);

const trackAndSpan = (
    state: VideoStagesConfig,
    trackIdx: number,
): { track: AudioTrack; span: AudioTrackSpan } | null => {
    const track = state.audioTracks?.[trackIdx];
    const span = track?.spans[0];
    return track && span ? { track, span } : null;
};

const spanGeometry = (
    span: AudioTrackSpan,
): { start: number; length: number; trim: number } | null => {
    if (
        span.timelineStartSeconds === null ||
        span.timelineLengthSeconds === null
    ) {
        return null;
    }
    return {
        start: span.timelineStartSeconds,
        length: span.timelineLengthSeconds,
        trim: span.sourceStartSeconds,
    };
};

const audioSnapTargets = (
    state: VideoStagesConfig,
    trackIdx: number,
): { primary: number[]; fallback: number[] } => {
    const above = trackIdx > 0 ? trackAndSpan(state, trackIdx - 1) : null;
    const aboveGeometry = above ? spanGeometry(above.span) : null;
    return {
        primary: aboveGeometry
            ? [aboveGeometry.start, aboveGeometry.start + aboveGeometry.length]
            : [],
        fallback: timelineClipEdges(state.clips),
    };
};

const snapAudioPoint = (
    state: VideoStagesConfig,
    trackIdx: number,
    value: number,
    pps: number,
): number => {
    if (!getTimelineAuthoringSettings().snap) {
        return value;
    }
    const targets = audioSnapTargets(state, trackIdx);
    return snapPoint(
        value,
        targets.primary,
        targets.fallback,
        SNAP_THRESHOLD_PX / pps,
    );
};

const snapAudioMovedStart = (
    state: VideoStagesConfig,
    trackIdx: number,
    start: number,
    length: number,
    pps: number,
): number => {
    if (!getTimelineAuthoringSettings().snap) {
        return start;
    }
    const targets = audioSnapTargets(state, trackIdx);
    return snapMovedStart(
        start,
        length,
        targets.primary,
        targets.fallback,
        SNAP_THRESHOLD_PX / pps,
    );
};

const spanStyle = (
    start: number,
    length: number,
    total: number,
): { left: string; width: string } => {
    const geometry = laneSpanGeometry(start, length, total);
    return { left: `${geometry.left}%`, width: `${geometry.width}%` };
};

/**
 * The timeline-wide audio track: each root audio track owns one lane spanning the whole
 * timeline, and lanes may overlap freely in time (the backend mixes overlapping audio
 * additively). Left-edge resize keeps the end fixed and the source offset FOLLOWS the edge,
 * floored at 0.
 */
export const createTimelineAudioSegmentTrack =
    (): TimelineAudioSegmentTrack => {
        let body: HTMLElement | null = null;
        let unregister: (() => void) | null = null;
        let unbindClick: (() => void) | null = null;

        const commit = (
            sourceToken: string,
            trackIdx: number,
            mutate: (
                state: VideoStagesConfig,
                track: AudioTrack,
                span: AudioTrackSpan,
            ) => void,
        ): boolean => {
            if (isStaleToken(sourceToken)) {
                return false;
            }
            const state = structuredClone(getState());
            const found = trackAndSpan(state, trackIdx);
            if (!found) {
                return false;
            }
            mutate(state, found.track, found.span);
            saveState(state, { origin: "audio-segment-track" });
            setSelection({ kind: "audio-track", trackIdx });
            return true;
        };

        const onPress = (
            event: MouseEvent,
            boundBody: HTMLElement,
        ): GestureSession | null => {
            if (!(event.target instanceof Element)) {
                return null;
            }
            const element = event.target.closest(
                ".vst-audio-seg[data-track-idx]",
            );
            if (element instanceof HTMLElement) {
                const trackIdx = parseIntAttr(element, "data-track-idx");
                if (trackIdx === null) {
                    return null;
                }
                if (event.shiftKey) {
                    event.preventDefault();
                    return claimOnly();
                }
                const state = getState();
                const found = trackAndSpan(state, trackIdx);
                const press = found ? spanGeometry(found.span) : null;
                const total = timelineDuration(state);
                if (!press || total <= 0) {
                    return null;
                }
                const originalLeft = element.style.left;
                const originalWidth = element.style.width;
                const sourceToken = readStateToken();
                const edgeElement = event.target.closest(
                    "[data-vst-audio-seg-edge]",
                );
                const edge =
                    edgeElement?.getAttribute("data-vst-audio-seg-edge") ===
                    "left"
                        ? "left"
                        : edgeElement
                          ? "right"
                          : null;
                event.preventDefault();

                const restore = (): void => {
                    element.style.left = originalLeft;
                    element.style.width = originalWidth;
                };
                const rightLength = (delta: number, pps: number): number => {
                    const rawLength = clamp(
                        press.length + delta,
                        AUDIO_SEGMENT_MIN_LENGTH,
                        total - press.start,
                    );
                    const snappedEnd = snapAudioPoint(
                        state,
                        trackIdx,
                        press.start + rawLength,
                        pps,
                    );
                    return clamp(
                        snappedEnd - press.start,
                        AUDIO_SEGMENT_MIN_LENGTH,
                        total - press.start,
                    );
                };
                const leftStart = (delta: number, pps: number): number => {
                    const end = press.start + press.length;
                    const lo = Math.max(0, press.start - press.trim);
                    const rawStart = clamp(
                        press.start + delta,
                        lo,
                        end - AUDIO_SEGMENT_MIN_LENGTH,
                    );
                    return clamp(
                        snapAudioPoint(state, trackIdx, rawStart, pps),
                        lo,
                        end - AUDIO_SEGMENT_MIN_LENGTH,
                    );
                };
                const movedStart = (delta: number, pps: number): number => {
                    const rawStart = clamp(
                        press.start + delta,
                        0,
                        Math.max(0, total - press.length),
                    );
                    return clamp(
                        snapAudioMovedStart(
                            state,
                            trackIdx,
                            rawStart,
                            press.length,
                            pps,
                        ),
                        0,
                        Math.max(0, total - press.length),
                    );
                };
                return {
                    threshold: DRAG_THRESHOLD_PX,
                    onMove: (ctx) => {
                        boundBody.classList.add("vst-audio-seg-dragging");
                        const pps = livePxPerSecond(boundBody);
                        const delta = ctx.dx / pps;
                        if (edge === "right") {
                            element.style.width = spanStyle(
                                press.start,
                                rightLength(delta, pps),
                                total,
                            ).width;
                        } else if (edge === "left") {
                            const end = press.start + press.length;
                            const start = leftStart(delta, pps);
                            const style = spanStyle(start, end - start, total);
                            element.style.left = style.left;
                            element.style.width = style.width;
                        } else {
                            element.style.left = spanStyle(
                                movedStart(delta, pps),
                                press.length,
                                total,
                            ).left;
                        }
                    },
                    onCommit: (ctx) => {
                        boundBody.classList.remove("vst-audio-seg-dragging");
                        const pps = livePxPerSecond(boundBody);
                        const delta = ctx.dx / pps;
                        commit(
                            sourceToken,
                            trackIdx,
                            (_state, _track, span) => {
                                if (edge === "right") {
                                    span.timelineLengthSeconds = roundToTenth(
                                        rightLength(delta, pps),
                                    );
                                } else if (edge === "left") {
                                    const end = press.start + press.length;
                                    const start = leftStart(delta, pps);
                                    span.timelineStartSeconds =
                                        roundToTenth(start);
                                    span.timelineLengthSeconds = roundToTenth(
                                        end - start,
                                    );
                                    span.sourceStartSeconds = roundToTenth(
                                        press.trim + (start - press.start),
                                    );
                                } else {
                                    span.timelineStartSeconds = roundToTenth(
                                        movedStart(delta, pps),
                                    );
                                }
                            },
                        );
                    },
                    onTap: restore,
                    onCancel: () => {
                        restore();
                        boundBody.classList.remove("vst-audio-seg-dragging");
                    },
                };
            }

            const lane = event.target.closest(
                ".vst-audio-seg-lane[data-vst-audio-seg-add]:not([data-clip-idx])",
            );
            if (!(lane instanceof HTMLElement)) {
                return null;
            }
            const state = getState();
            const total = timelineDuration(state);
            if (total < AUDIO_SEGMENT_MIN_LENGTH) {
                return null;
            }
            const rect = lane.getBoundingClientRect();
            const pps = livePxPerSecond(boundBody);
            const trackIdxAtPress = state.audioTracks?.length ?? 0;
            const startAtPress = snapAudioPoint(
                state,
                trackIdxAtPress,
                clamp((event.clientX - rect.left) / pps, 0, total),
                pps,
            );
            const sourceToken = readStateToken();
            let ghost: HTMLElement | null = null;
            event.preventDefault();

            const removeGhost = (): void => {
                ghost?.remove();
                ghost = null;
            };
            const timeAt = (clientX: number): number => {
                const livePps = livePxPerSecond(boundBody);
                return snapAudioPoint(
                    state,
                    trackIdxAtPress,
                    clamp((clientX - rect.left) / livePps, 0, total),
                    livePps,
                );
            };
            const create = (endAt: number | null): void => {
                if (isStaleToken(sourceToken)) {
                    return;
                }
                const geometry = createDefaultOrDraggedSpan(
                    startAtPress,
                    endAt,
                    0,
                    total,
                    AUDIO_SEGMENT_MIN_LENGTH,
                    AUDIO_SEGMENT_DEFAULT_LENGTH,
                );
                if (!geometry) {
                    return;
                }
                const next = structuredClone(getState());
                next.audioTracks ??= [];
                const trackIdx = next.audioTracks.length;
                next.audioTracks.push({
                    id: createEntityId("audio_track"),
                    source: {
                        kind: "Upload",
                        reference: "",
                        uploadedAudio: null,
                    },
                    volume: AUDIO_SEGMENT_VOLUME_DEFAULT,
                    spans: [
                        {
                            id: createEntityId("audio_span"),
                            timelineStartSeconds: roundToTenth(geometry.start),
                            timelineLengthSeconds: roundToTenth(
                                geometry.length,
                            ),
                            sourceStartSeconds: 0,
                        },
                    ],
                });
                saveState(next, { origin: "audio-segment-track" });
                setSelection({ kind: "audio-track", trackIdx });
            };

            return {
                threshold: DRAG_THRESHOLD_PX,
                suppressTapClick: true,
                onMove: (ctx) => {
                    boundBody.classList.add("vst-audio-seg-dragging");
                    const now = timeAt(ctx.event.clientX);
                    const start = Math.min(startAtPress, now);
                    const end = Math.max(startAtPress, now);
                    if (!ghost) {
                        ghost = document.createElement("div");
                        ghost.className = "vst-audio-seg-ghost";
                        lane.appendChild(ghost);
                    }
                    const style = spanStyle(start, end - start, total);
                    ghost.style.left = style.left;
                    ghost.style.width = style.width;
                },
                onCommit: (ctx) => {
                    boundBody.classList.remove("vst-audio-seg-dragging");
                    removeGhost();
                    create(timeAt(ctx.event.clientX));
                },
                onTap: () => {
                    removeGhost();
                    create(null);
                },
                onCancel: () => {
                    removeGhost();
                    boundBody.classList.remove("vst-audio-seg-dragging");
                },
            };
        };

        const onClick = (event: Event): void => {
            if (!(event.target instanceof Element)) {
                return;
            }
            const span = event.target.closest(".vst-audio-seg[data-track-idx]");
            if (!(span instanceof HTMLElement)) {
                return;
            }
            event.stopImmediatePropagation();
            const trackIdx = parseIntAttr(span, "data-track-idx");
            if (trackIdx === null || !trackAndSpan(getState(), trackIdx)) {
                return;
            }
            if ((event as MouseEvent).shiftKey) {
                const next = structuredClone(getState());
                next.audioTracks?.splice(trackIdx, 1);
                saveState(next, { origin: "audio-segment-track" });
                const count = next.audioTracks?.length ?? 0;
                setSelection(
                    count > 0
                        ? {
                              kind: "audio-track",
                              trackIdx: Math.min(trackIdx, count - 1),
                          }
                        : { kind: "none" },
                );
                return;
            }
            setSelection({ kind: "audio-track", trackIdx });
        };

        const onKeyDown = (event: Event): void => {
            const keyboard = event as KeyboardEvent;
            if (
                !isActivateKey(keyboard) ||
                !(keyboard.target instanceof Element)
            ) {
                return;
            }
            const span = keyboard.target.closest(
                ".vst-audio-seg[data-track-idx]",
            );
            if (!(span instanceof HTMLElement)) {
                return;
            }
            const trackIdx = parseIntAttr(span, "data-track-idx");
            if (trackIdx === null) {
                return;
            }
            keyboard.preventDefault();
            keyboard.stopImmediatePropagation();
            setSelection({ kind: "audio-track", trackIdx });
        };

        return {
            attach: (nextBody, router) => {
                if (body === nextBody) {
                    return;
                }
                unbindClick?.();
                unregister?.();
                body = nextBody;
                unregister = router.register({
                    id: "timeline-audio-segment",
                    priority: 40,
                    onPress,
                });
                nextBody.addEventListener("click", onClick);
                nextBody.addEventListener("keydown", onKeyDown);
                unbindClick = () => {
                    nextBody.removeEventListener("click", onClick);
                    nextBody.removeEventListener("keydown", onKeyDown);
                };
            },
            dispose: () => {
                unbindClick?.();
                unregister?.();
                unbindClick = null;
                unregister = null;
                body = null;
            },
        };
    };
