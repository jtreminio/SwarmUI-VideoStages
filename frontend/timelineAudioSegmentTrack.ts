import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    AUDIO_SEGMENT_VOLUME_DEFAULT,
} from "./constants";
import type { GestureRouter } from "./gestureRouter";
import { createEntityId } from "./identity";
import { getState, saveState } from "./persistence";
import { selectionAfterRemoval } from "./selection";
import { timelineClipEdges } from "./timelineSnap";
import type { AudioTrack, AudioTrackSpan, VideoStagesConfig } from "./types";
import { roundToTenth } from "./utils";
import {
    createDefaultOrDraggedSpan,
    createWindowTrack,
    type PressSpan,
    resizeSpanEdge,
    type WindowTrackScope,
} from "./windowTrack";

export interface TimelineAudioSegmentTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

const timelineDuration = (state: VideoStagesConfig): number =>
    state.clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);

/** Each track owns one lane-spanning segment; `sourceStartSeconds` is its trim. */
const pressSpanOf = (span: AudioTrackSpan | undefined): PressSpan | null =>
    span &&
    span.timelineStartSeconds !== null &&
    span.timelineLengthSeconds !== null
        ? {
              start: span.timelineStartSeconds,
              length: span.timelineLengthSeconds,
              trim: span.sourceStartSeconds,
          }
        : null;

const blankTrack = (): AudioTrack => ({
    id: createEntityId("audio_track"),
    source: { kind: "Upload", reference: "", uploadedAudio: null },
    volume: AUDIO_SEGMENT_VOLUME_DEFAULT,
    spans: [],
});

/**
 * Audio tracks are owned by the document, not by a clip: they are keyed by
 * `data-track-idx` and their lane is the whole timeline. A create gesture
 * materialises the track itself, and removing the last span removes the track.
 */
const audioTrackScope = (): WindowTrackScope<AudioTrack> => ({
    read: (ownerIdx) => {
        const state = getState();
        const owner = state.audioTracks?.[ownerIdx];
        return owner
            ? { owner, ownerIdx, duration: timelineDuration(state) }
            : null;
    },
    // The blank lane carries no index: a create appends a new track.
    resolveLane: () => {
        const state = getState();
        return {
            owner: null,
            ownerIdx: state.audioTracks?.length ?? 0,
            duration: timelineDuration(state),
        };
    },
    write: (ownerIdx, create, mutate) => {
        const state = getState();
        state.audioTracks ??= [];
        const tracks = state.audioTracks;
        if (create && ownerIdx === tracks.length) {
            tracks.push(blankTrack());
        }
        const owner = tracks[ownerIdx];
        if (!owner) {
            return false;
        }
        const applied = mutate({
            owner,
            ownerIdx,
            duration: timelineDuration(state),
            removeOwner: () => {
                tracks.splice(ownerIdx, 1);
                return tracks.length;
            },
        });
        if (!applied) {
            return false;
        }
        saveState(state, { origin: "audio-segment-track" });
        return true;
    },
});

/**
 * The timeline-wide audio track: each root audio track owns one lane spanning
 * the whole timeline, and lanes may overlap freely in time (the backend mixes
 * overlapping audio additively). Left-edge resize keeps the end fixed and the
 * source offset FOLLOWS the edge, floored at 0.
 */
export const createTimelineAudioSegmentTrack = (): TimelineAudioSegmentTrack =>
    createWindowTrack<AudioTrack>({
        routeId: "timeline-audio-segment",
        priority: 40,
        scope: audioTrackScope(),
        spanSelector: ".vst-audio-seg[data-track-idx]",
        ownerIdxAttr: "data-track-idx",
        itemIdxAttr: null,
        edgeSelector: "[data-vst-audio-seg-edge]",
        edgeAttr: "data-vst-audio-seg-edge",
        laneSelector:
            ".vst-audio-seg-lane[data-vst-audio-seg-add]:not([data-clip-idx])",
        draggingClass: "vst-audio-seg-dragging",
        ghostClass: "vst-audio-seg-ghost",
        unit: "pct",
        keyboardSelect: true,
        // The segment sits on the audio row; its clicks must not bubble
        // into that row's select handler.
        isolateClicks: true,
        readSpan: ({ owner }) => pressSpanOf(owner.spans[0]),
        canCreate: ({ duration }) => duration >= AUDIO_SEGMENT_MIN_LENGTH,
        // Segments snap to the track immediately above before falling back
        // to the clip boundaries underneath them.
        snapTargets: (ownerIdx) => {
            const state = getState();
            const above = pressSpanOf(
                ownerIdx > 0
                    ? state.audioTracks?.[ownerIdx - 1]?.spans[0]
                    : undefined,
            );
            return {
                primary: above ? [above.start, above.start + above.length] : [],
                fallback: timelineClipEdges(state.clips),
            };
        },
        moveTargetStart: ({ duration }, _itemIdx, press, desiredStart) =>
            Math.min(
                Math.max(desiredStart, 0),
                Math.max(0, duration - press.length),
            ),
        writeMove: ({ owner }, _itemIdx, _press, start) => {
            const span = owner.spans[0];
            if (span) {
                span.timelineStartSeconds = roundToTenth(start);
            }
        },
        // A left resize may run back to the start of the untrimmed source,
        // so its wall sits `trim` seconds before the pressed start.
        resizeTarget: ({ duration }, _itemIdx, edge, press, delta) =>
            resizeSpanEdge(
                edge,
                press,
                delta,
                AUDIO_SEGMENT_MIN_LENGTH,
                Math.max(0, press.start - press.trim),
                duration,
            ),
        writeResize: ({ owner }, _itemIdx, edge, press, geom) => {
            const span = owner.spans[0];
            if (!span) {
                return;
            }
            span.timelineStartSeconds = roundToTenth(geom.start);
            span.timelineLengthSeconds = roundToTenth(geom.length);
            if (edge === "left") {
                span.sourceStartSeconds = roundToTenth(
                    press.trim + (geom.start - press.start),
                );
            }
        },
        createSpan: ({ owner, ownerIdx, duration }, startSec, endSec) => {
            const geom = createDefaultOrDraggedSpan(
                startSec,
                endSec,
                0,
                duration,
                AUDIO_SEGMENT_MIN_LENGTH,
                AUDIO_SEGMENT_DEFAULT_LENGTH,
            );
            if (!geom) {
                return null;
            }
            owner.spans.push({
                id: createEntityId("audio_span"),
                timelineStartSeconds: roundToTenth(geom.start),
                timelineLengthSeconds: roundToTenth(geom.length),
                sourceStartSeconds: 0,
            });
            return { kind: "audio-track", trackIdx: ownerIdx };
        },
        // A track exists only for its segments: deleting the last one
        // deletes the track, and the selection moves to a sibling track.
        deleteItem: ({ owner, ownerIdx, removeOwner }, itemIdx) => {
            if (!owner.spans[itemIdx]) {
                return null;
            }
            owner.spans.splice(itemIdx, 1);
            if (owner.spans.length > 0) {
                return { kind: "audio-track", trackIdx: ownerIdx };
            }
            return selectionAfterRemoval(
                ownerIdx,
                removeOwner?.() ?? 0,
                (index) => ({ kind: "audio-track", trackIdx: index }),
                { kind: "none" },
            );
        },
        selectionFor: (trackIdx) => ({ kind: "audio-track", trackIdx }),
    });
