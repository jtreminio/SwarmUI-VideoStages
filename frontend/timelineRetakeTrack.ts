import {
    clamp,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
} from "./constants";
import type { GestureRouter } from "./gestureRouter";
import { clipDurationOf } from "./trackDomUtils";
import type { Clip } from "./types";
import { roundToTenth } from "./utils";
import {
    createDefaultOrDraggedSpan,
    createWindowTrack,
    type PressSpan,
    resizeSpanEdge,
    type SpanGeom,
} from "./windowTrack";

export interface TimelineRetakeTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

/**
 * The retake window track: at most ONE retake per clip, living on a mini-lane
 * under the clip region. The degenerate single-span case of the shared
 * window-track machinery — the span index is always 0.
 */
export const createTimelineRetakeTrack = (): TimelineRetakeTrack =>
    createWindowTrack({
        routeId: "retake",
        priority: 50,
        origin: "retake-track",
        spanSelector: ".vst-retake[data-clip-idx]",
        itemIdxAttr: null,
        edgeSelector: "[data-vst-retake-edge]",
        edgeAttr: "data-vst-retake-edge",
        laneSelector: ".vst-retake-lane[data-vst-retake-add]",
        draggingClass: "vst-retake-dragging",
        ghostClass: "vst-retake-ghost",
        unit: "pct",
        keyboardSelect: true,
        // The retake sits inside the clip region; its clicks must not bubble
        // into the region's clip-select handler.
        isolateClicks: true,
        readSpan: (clip: Clip): PressSpan | null =>
            clip.retake
                ? {
                      start: clip.retake.startSeconds,
                      length: clip.retake.lengthSeconds,
                      trim: 0,
                  }
                : null,
        canCreate: (clip) => !clip.retake,
        moveTargetStart: (clip, _itemIdx, press, desiredStart) => {
            const clipDur = clipDurationOf(clip);
            const length = Math.min(press.length, clipDur);
            return clamp(desiredStart, 0, Math.max(0, clipDur - length));
        },
        writeMove: (clip, _itemIdx, press, start) => {
            if (!clip.retake) {
                return;
            }
            const clipDur = clipDurationOf(clip);
            const length = Math.min(press.length, clipDur);
            clip.retake.startSeconds = roundToTenth(start);
            clip.retake.lengthSeconds = roundToTenth(
                Math.min(length, clipDur - clip.retake.startSeconds),
            );
        },
        resizeTarget: (clip, _itemIdx, edge, press, deltaSec): SpanGeom =>
            resizeSpanEdge(
                edge,
                press,
                deltaSec,
                RETAKE_MIN_DURATION,
                0,
                clipDurationOf(clip),
            ),
        writeResize: (clip, _itemIdx, _edge, _press, geom) => {
            if (!clip.retake) {
                return;
            }
            clip.retake.startSeconds = roundToTenth(geom.start);
            clip.retake.lengthSeconds = roundToTenth(geom.length);
        },
        // A plain tap places a default-length window at the pressed time; a
        // drag sizes it.
        createSpan: (clip, clipIdx, startSec, endSec) => {
            const geom = createDefaultOrDraggedSpan(
                startSec,
                endSec,
                0,
                clipDurationOf(clip),
                RETAKE_MIN_DURATION,
                RETAKE_DEFAULT_DURATION,
            );
            if (!geom) {
                return null;
            }
            clip.retake = {
                startSeconds: roundToTenth(geom.start),
                lengthSeconds: roundToTenth(geom.length),
                strength: RETAKE_STRENGTH_DEFAULT,
            };
            return { kind: "retake", clipIdx };
        },
        deleteItem: (clip) => {
            if (!clip.retake) {
                return false;
            }
            clip.retake = null;
            return true;
        },
        selectionFor: (clipIdx) => ({ kind: "retake", clipIdx }),
    });
