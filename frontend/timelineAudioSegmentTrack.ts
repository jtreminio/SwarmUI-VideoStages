import type { CapabilityViewResolver } from "./architectures/policy";
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
import {
    clipDurationOf,
    isActivateKey,
    isStaleToken,
    livePxPerSecond,
    parseIntAttr,
} from "./trackDomUtils";
import type {
    AudioSegment,
    AudioTrack,
    AudioTrackSpan,
    Clip,
    VideoStagesConfig,
} from "./types";
import { roundToTenth } from "./utils";
import {
    createDefaultOrDraggedSpan,
    createWindowTrack,
    type PressSpan,
    resizeSpanEdge,
    type SpanGeom,
} from "./windowTrack";

export interface TimelineAudioSegmentTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

const segmentOf = (clip: Clip, segIdx: number): AudioSegment | undefined =>
    clip.audioSegments?.[segIdx];

/**
 * The Tier-2 audio-segment track. Every segment lives on its own lane, so
 * segments may overlap freely in time (the backend mixes overlapping audio
 * additively) — the only walls are the clip's own [0, clipDuration]. The
 * array index IS the lane: segments are appended, never sorted.
 *
 * Left-edge resize keeps the end fixed and the source trim FOLLOWS the edge
 * (dragging right trims the head, dragging left un-trims) but floors at 0:
 * once fully un-trimmed, moving further left simply starts the segment
 * earlier.
 */
const createLegacyTimelineAudioSegmentTrack = (
    getCapabilities?: () => CapabilityViewResolver,
): TimelineAudioSegmentTrack =>
    createWindowTrack({
        routeId: "audio-segment",
        priority: 40,
        origin: "audio-segment-track",
        spanSelector: ".vst-audio-seg[data-clip-idx]",
        itemIdxAttr: "data-seg-idx",
        edgeSelector: "[data-vst-audio-seg-edge]",
        edgeAttr: "data-vst-audio-seg-edge",
        laneSelector: ".vst-audio-seg-lane[data-vst-audio-seg-add]",
        draggingClass: "vst-audio-seg-dragging",
        ghostClass: "vst-audio-seg-ghost",
        unit: "pct",
        keyboardSelect: true,
        // Segments sit on the audio row; their clicks must not bubble into
        // the audio clip's select handler.
        isolateClicks: true,
        readSpan: (clip, segIdx): PressSpan | null => {
            const segment = segmentOf(clip, segIdx);
            return segment
                ? {
                      start: segment.startSeconds,
                      length: segment.lengthSeconds,
                      trim: segment.trimStartSeconds,
                  }
                : null;
        },
        canEdit: (clip) =>
            getCapabilities?.().forClip(clip).decision("audioSegments")
                .supported ?? true,
        canCreate: (clip) =>
            getCapabilities?.().forClip(clip).decision("audioSegments")
                .supported ?? true,
        moveTargetStart: (clip, _segIdx, press, desiredStart) => {
            const clipDur = clipDurationOf(clip);
            const length = Math.min(press.length, clipDur);
            return clamp(desiredStart, 0, Math.max(0, clipDur - length));
        },
        writeMove: (clip, segIdx, press, start) => {
            const segment = segmentOf(clip, segIdx);
            if (!segment) {
                return;
            }
            const clipDur = clipDurationOf(clip);
            const length = Math.min(press.length, clipDur);
            segment.startSeconds = roundToTenth(start);
            segment.lengthSeconds = roundToTenth(
                Math.min(length, clipDur - segment.startSeconds),
            );
        },
        resizeTarget: (clip, _segIdx, edge, press, deltaSec): SpanGeom =>
            // Left edge: trim follows, lower wall dips below 0 (un-trims further).
            // Right edge: walled by clip end.
            resizeSpanEdge(
                edge,
                press,
                deltaSec,
                AUDIO_SEGMENT_MIN_LENGTH,
                Math.min(
                    0,
                    press.start + press.length - AUDIO_SEGMENT_MIN_LENGTH,
                ),
                clipDurationOf(clip),
            ),
        writeResize: (clip, segIdx, edge, press, geom) => {
            const segment = segmentOf(clip, segIdx);
            if (!segment) {
                return;
            }
            if (edge === "right") {
                segment.startSeconds = roundToTenth(press.start);
                segment.lengthSeconds = roundToTenth(geom.length);
                return;
            }
            segment.startSeconds = roundToTenth(geom.start);
            segment.trimStartSeconds = roundToTenth(
                Math.max(0, press.trim + (geom.start - press.start)),
            );
            segment.lengthSeconds = roundToTenth(geom.length);
        },
        createSpan: (clip, clipIdx, startSec, endSec) => {
            const geom = createDefaultOrDraggedSpan(
                startSec,
                endSec,
                0,
                clipDurationOf(clip),
                AUDIO_SEGMENT_MIN_LENGTH,
                AUDIO_SEGMENT_DEFAULT_LENGTH,
            );
            if (!geom) {
                return null;
            }
            const segment: AudioSegment = {
                source: null,
                startSeconds: roundToTenth(geom.start),
                trimStartSeconds: 0,
                lengthSeconds: roundToTenth(geom.length),
                volume: AUDIO_SEGMENT_VOLUME_DEFAULT,
            };
            const segments = [...(clip.audioSegments ?? []), segment];
            clip.audioSegments = segments;
            return {
                kind: "audio-segment",
                clipIdx,
                segIdx: segments.length - 1,
            };
        },
        deleteItem: (clip, segIdx) => {
            if (!segmentOf(clip, segIdx)) {
                return false;
            }
            clip.audioSegments = clip.audioSegments.filter(
                (_, i) => i !== segIdx,
            );
            return true;
        },
        selectionFor: (clipIdx, segIdx) => ({
            kind: "audio-segment",
            clipIdx,
            segIdx,
        }),
    });

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

const pct = (seconds: number, total: number): string =>
    `${total > 0 ? (seconds / total) * 100 : 0}%`;

const createGlobalTimelineAudioSegmentTrack = (): TimelineAudioSegmentTrack => {
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
        const element = event.target.closest(".vst-audio-seg[data-track-idx]");
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
                edgeElement?.getAttribute("data-vst-audio-seg-edge") === "left"
                    ? "left"
                    : edgeElement
                      ? "right"
                      : null;
            event.preventDefault();

            const restore = (): void => {
                element.style.left = originalLeft;
                element.style.width = originalWidth;
            };
            return {
                threshold: DRAG_THRESHOLD_PX,
                onMove: (ctx) => {
                    boundBody.classList.add("vst-audio-seg-dragging");
                    const delta = ctx.dx / livePxPerSecond(boundBody);
                    if (edge === "right") {
                        const length = clamp(
                            press.length + delta,
                            AUDIO_SEGMENT_MIN_LENGTH,
                            total - press.start,
                        );
                        element.style.width = pct(length, total);
                    } else if (edge === "left") {
                        const end = press.start + press.length;
                        const start = clamp(
                            press.start + delta,
                            Math.max(0, press.start - press.trim),
                            end - AUDIO_SEGMENT_MIN_LENGTH,
                        );
                        element.style.left = pct(start, total);
                        element.style.width = pct(end - start, total);
                    } else {
                        const start = clamp(
                            press.start + delta,
                            0,
                            Math.max(0, total - press.length),
                        );
                        element.style.left = pct(start, total);
                    }
                },
                onCommit: (ctx) => {
                    boundBody.classList.remove("vst-audio-seg-dragging");
                    const delta = ctx.dx / livePxPerSecond(boundBody);
                    commit(sourceToken, trackIdx, (_state, _track, span) => {
                        if (edge === "right") {
                            span.timelineLengthSeconds = roundToTenth(
                                clamp(
                                    press.length + delta,
                                    AUDIO_SEGMENT_MIN_LENGTH,
                                    total - press.start,
                                ),
                            );
                        } else if (edge === "left") {
                            const end = press.start + press.length;
                            const start = clamp(
                                press.start + delta,
                                Math.max(0, press.start - press.trim),
                                end - AUDIO_SEGMENT_MIN_LENGTH,
                            );
                            span.timelineStartSeconds = roundToTenth(start);
                            span.timelineLengthSeconds = roundToTenth(
                                end - start,
                            );
                            span.sourceStartSeconds = roundToTenth(
                                press.trim + (start - press.start),
                            );
                        } else {
                            span.timelineStartSeconds = roundToTenth(
                                clamp(
                                    press.start + delta,
                                    0,
                                    Math.max(0, total - press.length),
                                ),
                            );
                        }
                    });
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
        const startAtPress = clamp((event.clientX - rect.left) / pps, 0, total);
        const sourceToken = readStateToken();
        let ghost: HTMLElement | null = null;
        event.preventDefault();

        const removeGhost = (): void => {
            ghost?.remove();
            ghost = null;
        };
        const timeAt = (clientX: number): number =>
            clamp((clientX - rect.left) / livePxPerSecond(boundBody), 0, total);
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
                        firstClipId: null,
                        lastClipId: null,
                        timelineStartSeconds: roundToTenth(geometry.start),
                        timelineLengthSeconds: roundToTenth(geometry.length),
                        sourceStartSeconds: 0,
                        clipStartOffsetSeconds: null,
                        clipLengthSeconds: null,
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
                ghost.style.left = pct(start, total);
                ghost.style.width = pct(end - start, total);
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
        if (!isActivateKey(keyboard) || !(keyboard.target instanceof Element)) {
            return;
        }
        const span = keyboard.target.closest(".vst-audio-seg[data-track-idx]");
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

export const createTimelineAudioSegmentTrack = (
    getCapabilities?: () => CapabilityViewResolver,
): TimelineAudioSegmentTrack => {
    const legacy = createLegacyTimelineAudioSegmentTrack(getCapabilities);
    const global = createGlobalTimelineAudioSegmentTrack();
    return {
        attach: (body, router) => {
            legacy.attach(body, router);
            global.attach(body, router);
        },
        dispose: () => {
            legacy.dispose();
            global.dispose();
        },
    };
};
