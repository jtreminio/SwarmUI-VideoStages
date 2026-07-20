import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    clamp,
} from "./constants";
import type { GestureRouter } from "./gestureRouter";
import { clipDurationOf } from "./trackDomUtils";
import type { AudioSegment, Clip } from "./types";
import { roundToTenth } from "./utils";
import {
    createWindowTrack,
    type PressSpan,
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
export const createTimelineAudioSegmentTrack = (): TimelineAudioSegmentTrack =>
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
        resizeTarget: (clip, _segIdx, edge, press, deltaSec): SpanGeom => {
            const clipDur = clipDurationOf(clip);
            if (edge === "right") {
                const end = clamp(
                    press.start + press.length + deltaSec,
                    press.start + AUDIO_SEGMENT_MIN_LENGTH,
                    clipDur,
                );
                return { start: press.start, length: end - press.start };
            }
            const end = press.start + press.length;
            const start = clamp(
                press.start + deltaSec,
                Math.min(0, end - AUDIO_SEGMENT_MIN_LENGTH),
                end - AUDIO_SEGMENT_MIN_LENGTH,
            );
            return { start, length: end - start };
        },
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
            const clipDur = clipDurationOf(clip);
            if (clipDur < AUDIO_SEGMENT_MIN_LENGTH) {
                return null;
            }
            let start: number;
            let length: number;
            if (endSec === null) {
                length = Math.min(AUDIO_SEGMENT_DEFAULT_LENGTH, clipDur);
                start = clamp(startSec, 0, clipDur - length);
            } else {
                const a = clamp(Math.min(startSec, endSec), 0, clipDur);
                const b = clamp(Math.max(startSec, endSec), 0, clipDur);
                start = a;
                length = Math.max(AUDIO_SEGMENT_MIN_LENGTH, b - a);
                if (start + length > clipDur) {
                    length = clipDur - start;
                }
            }
            if (length < AUDIO_SEGMENT_MIN_LENGTH) {
                return null;
            }
            const segment: AudioSegment = {
                source: null,
                startSeconds: roundToTenth(start),
                trimStartSeconds: 0,
                lengthSeconds: roundToTenth(length),
            };
            // Appended, never sorted: the array index IS the lane, and lanes
            // must not reshuffle as segments move around in time.
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
