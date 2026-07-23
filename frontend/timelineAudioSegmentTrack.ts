import type { CapabilityViewResolver } from "./architectures/policy";
import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    AUDIO_SEGMENT_VOLUME_DEFAULT,
    clamp,
} from "./constants";
import type { GestureRouter } from "./gestureRouter";
import { clipDurationOf } from "./trackDomUtils";
import type { AudioSegment, Clip } from "./types";
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
export const createTimelineAudioSegmentTrack = (
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
