import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { createGestureRouter, type GestureRouter } from "./gestureRouter";
import * as persistence from "./persistence";
import {
    createTimelineRetakeTrack,
    type TimelineRetakeTrack,
} from "./timelineRetakeTrack";
import type { Clip, Retake } from "./types";
import { getSelection, resetSelectionForTests } from "./uiState";

const PPS = 44;

interface ClipFixture {
    duration: number;
    retake?: Retake;
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    stages: [{}],
    refs: [],
    ...(clip.retake ? { retake: clip.retake } : {}),
});

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
};

// Minimal region + retake lane (the real markup's shape: the retake window
// lives in a per-clip lane BELOW the region) with the data-* hooks the module
// reads.
const renderRetake = (body: HTMLElement, clips: ClipFixture[]): void => {
    let cursor = 0;
    const parts: string[] = [];
    clips.forEach((clip, i) => {
        const startPx = cursor * PPS;
        const widthPx = clip.duration * PPS;
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
            `<div class="vst-retake-lane" data-vst-retake-add data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px">${overlay}</div>`,
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
        const left = cursor * PPS;
        const lane = body.querySelector<HTMLElement>(
            `.vst-retake-lane[data-clip-idx="${i}"]`,
        );
        if (lane) {
            lane.getBoundingClientRect = (() =>
                ({
                    left,
                    width: clip.duration * PPS,
                    right: left + clip.duration * PPS,
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

const el = (body: HTMLElement, selector: string): HTMLElement => {
    const found = body.querySelector<HTMLElement>(selector);
    if (!found) {
        throw new Error(`not found: ${selector}`);
    }
    return found;
};

const mouse = (type: string, clientX: number, shiftKey = false): MouseEvent =>
    new MouseEvent(type, {
        bubbles: true,
        clientX,
        clientY: 20,
        button: 0,
        shiftKey,
    });

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[0][0] as Clip[];

const savedRetake = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
    clipIdx = 0,
): Retake | null => savedClips(spy)[clipIdx].retake;

const RETAKE: Retake = { startSeconds: 2, lengthSeconds: 3, strength: 1 };

describe("createTimelineRetakeTrack (DOM gestures)", () => {
    let track: TimelineRetakeTrack | null = null;
    let router: GestureRouter | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        resetSelectionForTests();
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
        const body = makeBody();
        renderRetake(body, clips);
        track = createTimelineRetakeTrack();
        router = createGestureRouter();
        router.attach(body);
        track.attach(body, router);
        return body;
    };

    it("clicking the overlay selects the retake without saving", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = el(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mouseup", 200));
        overlay.dispatchEvent(mouse("click", 200));

        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("shift+click on the overlay deletes the retake", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = el(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("click", 200, true));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedRetake(saveSpy)).toBeNull();
    });

    it("dragging the overlay body moves the retake in time", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = el(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * PPS)); // +2s
        document.dispatchEvent(mouse("mouseup", 100 + 2 * PPS));

        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(4, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(3, 5);
        // The committed drag selects the dragged retake.
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
    });

    it("moving past the clip end clamps start so the window stays inside", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = el(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 20 * PPS)); // far right
        document.dispatchEvent(mouse("mouseup", 100 + 20 * PPS));

        const retake = savedRetake(saveSpy);
        // start clamped to duration - length = 10 - 3 = 7.
        expect(retake?.startSeconds).toBeCloseTo(7, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(3, 5);
    });

    it("resizing the right edge changes length and keeps start", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const edge = el(body, ".vst-retake-resize-r");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * PPS)); // +2s
        document.dispatchEvent(mouse("mouseup", 100 + 2 * PPS));

        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(2, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(5, 5);
    });

    it("resizing the left edge changes start and keeps the end fixed", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const edge = el(body, ".vst-retake-resize-l");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 1 * PPS)); // +1s
        document.dispatchEvent(mouse("mouseup", 100 + 1 * PPS));

        const retake = savedRetake(saveSpy);
        // start 2 -> 3; end stays at 5, so length 5 - 3 = 2.
        expect(retake?.startSeconds).toBeCloseTo(3, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(2, 5);
    });

    it("a no-op click on an edge does not move the overlay or save", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = el(body, ".vst-retake[data-clip-idx='0']");
        const before = overlay.style.left;
        const edge = el(body, ".vst-retake-resize-r");
        edge.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mouseup", 200)); // no movement

        expect(overlay.style.left).toBe(before);
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("Enter on the overlay selects the retake", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const overlay = el(body, ".vst-retake[data-clip-idx='0']");
        overlay.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );

        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
    });

    // ---- relay-prompt parity: lane create --------------------------------

    it("clicking the empty lane adds a default-length retake and selects it", () => {
        const body = setup([{ duration: 10 }]);
        const lane = el(body, ".vst-retake-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 3 * PPS)); // t = 3s
        document.dispatchEvent(mouse("mouseup", 3 * PPS));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(3, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(2, 5); // default length
        expect(retake?.strength).toBe(1);
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
    });

    it("click-dragging on the empty lane adds a retake sized to the drag", () => {
        const body = setup([{ duration: 10 }]);
        const lane = el(body, ".vst-retake-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 7 * PPS));
        document.dispatchEvent(mouse("mouseup", 7 * PPS));

        const retake = savedRetake(saveSpy);
        expect(retake?.startSeconds).toBeCloseTo(2, 5);
        expect(retake?.lengthSeconds).toBeCloseTo(5, 5);
    });

    it("a lane press does nothing when the clip already has a retake", () => {
        const body = setup([{ duration: 10, retake: RETAKE }]);
        const lane = el(body, ".vst-retake-lane[data-clip-idx='0']");
        // Press the lane OUTSIDE the existing window (retake spans [2,5]).
        lane.dispatchEvent(mouse("mousedown", 8 * PPS));
        document.dispatchEvent(mouse("mouseup", 8 * PPS));

        expect(saveSpy).not.toHaveBeenCalled();
    });
});
