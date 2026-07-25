import type { CapabilityViewResolver } from "./architectures/policy";
import {
    clamp,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
} from "./constants";
import type { GestureRouter } from "./gestureRouter";
import { roundToTenth } from "./utils";
import {
    clipWindowTrackScope,
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
export const createTimelineRetakeTrack = (
    getCapabilities?: () => CapabilityViewResolver,
): TimelineRetakeTrack =>
    createWindowTrack({
        routeId: "retake",
        priority: 50,
        scope: clipWindowTrackScope("retake-track"),
        spanSelector: ".vst-retake[data-clip-idx]",
        itemIdxAttr: null,
        edgeSelector: "[data-vst-retake-edge]",
        edgeAttr: "data-vst-retake-edge",
        laneSelector: ".vst-retake-lane[data-vst-retake-add]",
        draggingClass: "vst-retake-dragging",
        ghostClass: "vst-retake-ghost",
        unit: "pct",
        keyboardSelect: true,
        revealOnActivate: true,
        // The retake sits inside the clip region; its clicks must not bubble
        // into the region's clip-select handler.
        isolateClicks: true,
        readSpan: ({ owner }): PressSpan | null =>
            owner.retake
                ? {
                      start: owner.retake.startSeconds,
                      length: owner.retake.lengthSeconds,
                      trim: 0,
                  }
                : null,
        canEdit: ({ owner }) =>
            getCapabilities?.().forClip(owner).decision("retake").supported ??
            true,
        canCreate: ({ owner }) =>
            owner !== null &&
            !owner.retake &&
            (getCapabilities?.().forClip(owner).decision("retake").supported ??
                true),
        moveTargetStart: ({ duration }, _itemIdx, press, desiredStart) => {
            const length = Math.min(press.length, duration);
            return clamp(desiredStart, 0, Math.max(0, duration - length));
        },
        writeMove: ({ owner, duration }, _itemIdx, press, start) => {
            if (!owner.retake) {
                return;
            }
            const length = Math.min(press.length, duration);
            owner.retake.startSeconds = roundToTenth(start);
            owner.retake.lengthSeconds = roundToTenth(
                Math.min(length, duration - owner.retake.startSeconds),
            );
        },
        resizeTarget: (
            { duration },
            _itemIdx,
            edge,
            press,
            deltaSec,
        ): SpanGeom =>
            resizeSpanEdge(
                edge,
                press,
                deltaSec,
                RETAKE_MIN_DURATION,
                0,
                duration,
            ),
        writeResize: ({ owner }, _itemIdx, _edge, _press, geom) => {
            if (!owner.retake) {
                return;
            }
            owner.retake.startSeconds = roundToTenth(geom.start);
            owner.retake.lengthSeconds = roundToTenth(geom.length);
        },
        // A plain tap places a default-length window at the pressed time; a
        // drag sizes it.
        createSpan: ({ owner, ownerIdx, duration }, startSec, endSec) => {
            // The add hook and press-time canCreate guard normally make this
            // impossible. Re-check at commit time so a concurrent retake
            // cannot be overwritten between press and release.
            if (owner.retake) {
                return null;
            }
            const geom = createDefaultOrDraggedSpan(
                startSec,
                endSec,
                0,
                duration,
                RETAKE_MIN_DURATION,
                RETAKE_DEFAULT_DURATION,
            );
            if (!geom) {
                return null;
            }
            owner.retake = {
                startSeconds: roundToTenth(geom.start),
                lengthSeconds: roundToTenth(geom.length),
                strength: RETAKE_STRENGTH_DEFAULT,
            };
            return { kind: "retake", clipIdx: ownerIdx };
        },
        // The clip owns at most one retake, so its removal always falls back
        // to the clip itself.
        deleteItem: ({ owner, ownerIdx }) => {
            if (!owner.retake) {
                return null;
            }
            owner.retake = null;
            return { kind: "clip", clipIdx: ownerIdx, stageIdx: 0 };
        },
        selectionFor: (clipIdx) => ({ kind: "retake", clipIdx }),
    });
