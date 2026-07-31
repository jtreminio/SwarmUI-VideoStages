import type { CapabilityViewResolver } from "./architectures/policy";
import {
    clamp,
    PROMPT_WINDOW_DEFAULT_DURATION,
    PROMPT_WINDOW_MIN_DURATION,
} from "./constants";
import type { GestureRouter } from "./gestureRouter";
import { freeIntervalAt } from "./intervals";
import { getClips } from "./persistence/repository";
import { otherSpans } from "./promptWindowEdits";
import { selectionAfterRemoval, setSelection } from "./selection";
import { clipDurationOf, parseIntAttr } from "./trackDomUtils";
import type { Clip, PromptWindow } from "./types";
import { roundToTenth } from "./utils";
import {
    clipWindowTrackScope,
    createDefaultOrDraggedSpan,
    createWindowTrack,
    type PressSpan,
    resizeSpanEdge,
    type SpanGeom,
} from "./windowTrack";

const MAJOR_SELECTOR = ".vst-major-seg[data-clip-idx]";

export interface TimelinePromptTrack {
    attach(body: HTMLElement, router: GestureRouter): void;
    dispose(): void;
}

/** The free-interval walls around a window at its press-time position. */
const wallsFor = (
    clip: Clip,
    windowIdx: number,
    press: PressSpan,
): [number, number] => {
    const clipDur = clipDurationOf(clip);
    return freeIntervalAt(
        otherSpans(clip.promptWindows ?? [], windowIdx, clipDur),
        clipDur,
        press.start,
    );
};

/**
 * The two-part Prompt track's MINOR relay-window lane. Windows must not
 * overlap: moves and resizes clamp to the free interval between neighbouring
 * windows (`intervals.ts`), and creates only land in a gap wide enough for a
 * minimum-length window.
 */
export const createTimelinePromptTrack = (
    getCapabilities?: () => CapabilityViewResolver,
): TimelinePromptTrack =>
    createWindowTrack({
        routeId: "prompt-track",
        priority: 20,
        scope: clipWindowTrackScope("prompt-track"),
        spanSelector: ".vst-minor-seg[data-clip-idx]",
        itemIdxAttr: "data-window-idx",
        edgeSelector: "[data-vst-minor-edge]",
        edgeAttr: "data-vst-minor-edge",
        laneSelector: ".vst-minor-lane[data-clip-idx]",
        draggingClass: "vst-prompt-dragging",
        ghostClass: "vst-minor-ghost",
        unit: "px",
        keyboardSelect: false,
        isolateClicks: false,
        readSpan: ({ owner }, windowIdx): PressSpan | null => {
            const window = owner.promptWindows?.[windowIdx];
            return window
                ? { start: window.start, length: window.duration, trim: 0 }
                : null;
        },
        canEdit: ({ owner }) =>
            getCapabilities?.().forClip(owner).decision("promptRelay")
                .supported ?? true,
        canCreate: ({ owner }) =>
            owner !== null &&
            (getCapabilities?.().forClip(owner).decision("promptRelay")
                .supported ??
                true),
        moveTargetStart: ({ owner: clip }, windowIdx, press, desiredStart) => {
            const clipDur = clipDurationOf(clip);
            const [lo, hi] = wallsFor(clip, windowIdx, press);
            const dur = Math.min(press.length, clipDur);
            return clamp(desiredStart, lo, Math.max(lo, hi - dur));
        },
        writeMove: ({ owner: clip }, windowIdx, press, start) => {
            const window = clip.promptWindows?.[windowIdx];
            if (!window) {
                return;
            }
            const clipDur = clipDurationOf(clip);
            const [, hi] = wallsFor(clip, windowIdx, press);
            const dur = Math.min(press.length, clipDur);
            window.start = roundToTenth(start);
            window.duration = roundToTenth(
                Math.max(
                    PROMPT_WINDOW_MIN_DURATION,
                    Math.min(dur, hi - window.start),
                ),
            );
        },
        resizeTarget: (
            { owner: clip },
            windowIdx,
            edge,
            press,
            deltaSec,
        ): SpanGeom => {
            const clipDur = clipDurationOf(clip);
            const spans = otherSpans(
                clip.promptWindows ?? [],
                windowIdx,
                clipDur,
            );
            const [, hi] = freeIntervalAt(spans, clipDur, press.start);
            const end = press.start + press.length;
            const [lo] = freeIntervalAt(
                spans,
                clipDur,
                Math.max(0, end - 1e-3),
            );
            return resizeSpanEdge(
                edge,
                press,
                deltaSec,
                PROMPT_WINDOW_MIN_DURATION,
                lo,
                hi,
            );
        },
        writeResize: ({ owner: clip }, windowIdx, _edge, _press, geom) => {
            const window = clip.promptWindows?.[windowIdx];
            if (!window) {
                return;
            }
            window.start = roundToTenth(geom.start);
            window.duration = roundToTenth(geom.length);
        },
        createSpan: ({ owner: clip, ownerIdx: clipIdx }, startSec, endSec) => {
            const clipDur = clipDurationOf(clip);
            const spans = otherSpans(clip.promptWindows ?? [], -1, clipDur);
            const [lo, hi] = freeIntervalAt(spans, clipDur, startSec);
            const geom = createDefaultOrDraggedSpan(
                startSec,
                endSec,
                lo,
                hi,
                PROMPT_WINDOW_MIN_DURATION,
                PROMPT_WINDOW_DEFAULT_DURATION,
            );
            if (!geom) {
                return null;
            }
            const window: PromptWindow = {
                prompt: "",
                start: roundToTenth(geom.start),
                duration: roundToTenth(geom.length),
            };
            clip.promptWindows.push(window);
            clip.promptWindows.sort((x, y) => x.start - y.start);
            // Open the new window in the dock ready to type: selecting it
            // auto-expands the dock (via the strip's selection subscriber) and
            // focuses its editor.
            const newIdx = clip.promptWindows.indexOf(window);
            return newIdx >= 0
                ? { kind: "prompt-minor", clipIdx, windowIdx: newIdx }
                : null;
        },
        deleteItem: ({ owner: clip, ownerIdx: clipIdx }, windowIdx) => {
            if (!clip.promptWindows?.[windowIdx]) {
                return null;
            }
            clip.promptWindows.splice(windowIdx, 1);
            return selectionAfterRemoval(
                windowIdx,
                clip.promptWindows.length,
                (index) => ({
                    kind: "prompt-minor",
                    clipIdx,
                    windowIdx: index,
                }),
                { kind: "prompt-major", clipIdx },
            );
        },
        selectionFor: (clipIdx, windowIdx) => ({
            kind: "prompt-minor",
            clipIdx,
            windowIdx,
        }),
        // Clicks that land on the MAJOR (whole-clip prompt) row select it.
        onClickFallthrough: (_event, target) => {
            const major = target.closest(MAJOR_SELECTOR);
            if (!(major instanceof HTMLElement)) {
                return;
            }
            const clipIdx = parseIntAttr(major, "data-clip-idx");
            if (clipIdx === null || !getClips()[clipIdx]) {
                return;
            }
            if (
                getCapabilities &&
                !getCapabilities()
                    .forClip(getClips()[clipIdx])
                    .decision("majorPrompt").supported &&
                !getClips()[clipIdx].prompt.trim()
            ) {
                return;
            }
            setSelection({ kind: "prompt-major", clipIdx });
        },
    });
