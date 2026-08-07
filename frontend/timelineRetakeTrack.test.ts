import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { storedClip } from "./__test_helpers__/clipFixtures";
import {
    firstSavedClips,
    mountPromptBox,
    mountTimelineBody,
    mountVideoStagesData,
    mouse,
    requireEl,
    TIMELINE_PPS,
} from "./__test_helpers__/dom";
import { createGestureRouter, type GestureRouter } from "./gestureRouter";
import * as persistence from "./persistence/repository";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
    subscribeSelection,
} from "./selection";
import { setTimelineAuthoringSetting } from "./timelineAuthoringSettings";
import {
    createTimelineRetakeTrack,
    type TimelineRetakeTrack,
} from "./timelineRetakeTrack";
import type { Clip, Retake } from "./types";

interface ClipFixture {
    duration: number;
    retake?: Retake;
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> =>
    storedClip({
        duration: clip.duration,
        ...(clip.retake ? { retake: clip.retake } : {}),
    });

// Minimal region + retake lane (the real markup's shape: the retake window
// lives in a per-clip lane BELOW the region) with the data-* hooks the module
// reads.
const renderRetake = (body: HTMLElement, clips: ClipFixture[]): void => {
    let cursor = 0;
    const parts: string[] = [];
    clips.forEach((clip, i) => {
        const startPx = cursor * TIMELINE_PPS;
        const widthPx = clip.duration * TIMELINE_PPS;
        let overlay = "";
        if (clip.retake) {
            const leftPct = (clip.retake.startSeconds / clip.duration) * 100;
            const widthPct = (clip.retake.lengthSeconds / clip.duration) * 100;
            overlay =
                `<div class="vst-retake" data-vst-retake data-clip-idx="${i}" style="left:${leftPct}%;width:${widthPct}%" role="button" tabindex="0">` +
                `<span class="vst-retake-resize vst-retake-resize-l" data-vst-retake-edge="left"></span>` +
                `<span class="vst-retake-label"></span>` +
                `<span class="vst-retake-resize vst-retake-resize-r" data-vst-retake-edge="right"></span>` +
                `</div>`;
        }
        parts.push(
            `<div class="vst-region" data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px"></div>`,
            `<div class="vst-retake-lane"${clip.retake ? " data-vst-retake-full" : " data-vst-retake-add"} data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px">${overlay}</div>`,
        );
        cursor += clip.duration;
    });
    body.innerHTML =
        `<div class="vst-track-row vst-track-video"><div class="vst-track-cell">` +
        parts.join("") +
        `</div></div>`;
    // jsdom does no layout — stub each lane's rect from its clip offset.
    cursor = 0;
    clips.forEach((clip, i) => {
        const left = cursor * TIMELINE_PPS;
        const lane = body.querySelector<HTMLElement>(
            `.vst-retake-lane[data-clip-idx="${i}"]`,
        );
        if (lane) {
            lane.getBoundingClientRect = (() =>
                ({
                    left,
                    width: clip.duration * TIMELINE_PPS,
                    right: left + clip.duration * TIMELINE_PPS,
                    top: 100,
                    bottom: 120,
                    height: 20,
                    x: left,
                    y: 100,
                    toJSON: () => ({}),
                }) as DOMRect) as HTMLElement["getBoundingClientRect"];
        }
        cursor += clip.duration;
    });
};

const savedRetake = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
    clipIdx = 0,
): Retake | null => firstSavedClips<Clip[]>(spy)[clipIdx].retake;

const RETAKE: Retake = { startSeconds: 2, lengthSeconds: 3, strength: 1 };

describe("createTimelineRetakeTrack (DOM gestures)", () => {
    let track: TimelineRetakeTrack | null = null;
    let router: GestureRouter | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        resetSelectionForTests();
        localStorage.clear();
        saveSpy = jest
            .spyOn(persistence, "saveClips")
            .mockImplementation(() => {});
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        router?.dispose();
        router = null;
        jest.restoreAllMocks();
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    const setup = (clips: ClipFixture[]): HTMLElement => {
        mountVideoStagesData({ clips: clips.map(clipRecord) });
        mountPromptBox("");
        const body = mountTimelineBody();
        renderRetake(body, clips);
        track = createTimelineRetakeTrack();
        router = createGestureRouter();
        router.attach(body);
        track.attach(body, router);
        return body;
    };

    it("clicking the overlay selects the retake without saving", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mouseup", 200));
        overlay.dispatchEvent(mouse("click", 200));

        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("shift+click on the overlay deletes the retake", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("click", 200, { shiftKey: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedRetake(saveSpy)).toBeNull();
    });

    it("dragging the overlay body moves the retake in time", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 2 * TIMELINE_PPS));

        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(4, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(3, 5);
        // drag-commit selects the dragged retake
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
    });

    it("moving past the clip end clamps start so the window stays inside", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 20 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 20 * TIMELINE_PPS));

        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(7, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(3, 5);
    });

    it("snaps a moved window edge to the clip edge, unless Snap is off", () => {
        let body = setup([{ duration: 10, retake: RETAKE }]);
        let overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 4.9 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 4.9 * TIMELINE_PPS));
        expect(savedRetake(saveSpy)?.startSeconds).toBe(7);

        track?.dispose();
        router?.dispose();
        track = null;
        router = null;
        document.body.innerHTML = "";
        saveSpy.mockClear();
        setTimelineAuthoringSetting("snap", false);
        body = setup([{ duration: 10, retake: RETAKE }]);
        overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 4.9 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 4.9 * TIMELINE_PPS));
        expect(savedRetake(saveSpy)?.startSeconds).toBe(6.9);
    });

    it("resizing the right edge changes length and keeps start", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const edge = requireEl(body, ".vst-retake-resize-r");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 2 * TIMELINE_PPS));

        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(2, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(5, 5);
    });

    it("resizing the left edge changes start and keeps the end fixed", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const edge = requireEl(body, ".vst-retake-resize-l");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 1 * TIMELINE_PPS));

        const retake = savedRetake(saveSpy);
        // start 2 -> 3; end stays at 5, so length 5 - 3 = 2.
        expect(retake?.startSeconds).toBeCloseTo(3, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(2, 5);
    });

    it("a no-op click on an edge does not move the overlay or save", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        const before = overlay.style.left;
        const edge = requireEl(body, ".vst-retake-resize-r");
        edge.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mouseup", 200));

        expect(overlay.style.left).toBe(before);
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("Enter on the overlay selects the retake", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = requireEl(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );

        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
    });

    it("re-emits activation when the selected retake is clicked again", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        setSelection({ kind: "retake", clipIdx: 0 });
        const seen: unknown[] = [];
        const stop = subscribeSelection((selection) => seen.push(selection));
        try {
            requireEl(body, ".vst-retake[data-clip-idx='0']").dispatchEvent(
                mouse("click", 3 * TIMELINE_PPS),
            );
            expect(seen).toEqual([{ kind: "retake", clipIdx: 0 }]);
        } finally {
            stop();
        }
    });

    // relay-prompt parity: lane create

    it("clicking the empty lane adds a default-length retake and selects it", () => {
        const body = setup([{ duration: 10 }]);
        const lane = requireEl(body, ".vst-retake-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 3 * TIMELINE_PPS)); // t = 3s
        document.dispatchEvent(mouse("mouseup", 3 * TIMELINE_PPS));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(3, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(3, 5); // default length
        expect(retake?.strength).toBe(1);
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
    });

    it("click-dragging on the empty lane adds a retake sized to the drag", () => {
        const body = setup([{ duration: 10 }]);
        const lane = requireEl(body, ".vst-retake-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 7 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 7 * TIMELINE_PPS));

        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(2, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(5, 5);
    });

    it("a lane press does nothing when the clip already has a retake", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const lane = requireEl(body, ".vst-retake-lane[data-clip-idx='0']");
        // Press the lane OUTSIDE the existing window (retake spans [2,5]).
        lane.dispatchEvent(mouse("mousedown", 8 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 8 * TIMELINE_PPS));

        expect(saveSpy).not.toHaveBeenCalled();
    });
});
