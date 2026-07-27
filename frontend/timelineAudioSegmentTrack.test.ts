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
import { getSelection, resetSelectionForTests } from "./selection";
import {
    createTimelineAudioSegmentTrack,
    type TimelineAudioSegmentTrack,
} from "./timelineAudioSegmentTrack";
import { setTimelineAuthoringSetting } from "./timelineAuthoringSettings";
import type { VideoStagesConfig } from "./types";

const PPS = 44;

const clipRecord = (duration: number): Record<string, unknown> => ({
    duration,
    stages: [{}],
    refs: [],
});

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
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
        clientY: 12,
        button: 0,
        shiftKey,
    });

describe("timeline-wide audio segment gestures", () => {
    let track: TimelineAudioSegmentTrack | null = null;
    let router: GestureRouter | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveState>;

    const rootState = (
        withTrack = true,
        withJoin = false,
    ): Record<string, unknown> => {
        const clips = [clipRecord(3), clipRecord(4)];
        if (withJoin) {
            Object.assign(clips[0], {
                boundaryOut: "crossfade",
                boundaryOutOverlap: 24,
            });
        }
        return {
            schemaVersion: 5,
            clips,
            audioTracks: withTrack
                ? [
                      {
                          id: "track-global",
                          volume: 0.75,
                          source: {
                              kind: "AceStepFun",
                              reference: "audio0",
                              uploadedAudio: null,
                          },
                          spans: [
                              {
                                  id: "span-global",
                                  timelineStartSeconds: 2,
                                  timelineLengthSeconds: 3,
                                  sourceStartSeconds: 1,
                              },
                          ],
                      },
                  ]
                : [],
        };
    };

    const setupGlobal = (withTrack = true, withJoin = false): HTMLElement => {
        mountVideoStagesData(rootState(withTrack, withJoin));
        mountPromptBox("");
        const duration = withJoin ? 6 : 7;
        const body = makeBody();
        body.innerHTML =
            `<div class="vst-audio-seg-lane${withTrack ? "" : " vst-audio-seg-lane-blank"}" ` +
            `${withTrack ? 'data-track-idx="0"' : "data-vst-audio-seg-add"} style="left:0;width:${duration * PPS}px">` +
            (withTrack
                ? `<div class="vst-audio-seg" data-vst-audio-seg data-track-idx="0" style="left:${(2 / duration) * 100}%;width:${(3 / duration) * 100}%">` +
                  `<span data-vst-audio-seg-edge="left"></span><span data-vst-audio-seg-edge="right"></span></div>`
                : "") +
            `</div>`;
        const lane = body.querySelector<HTMLElement>(".vst-audio-seg-lane");
        if (lane) {
            lane.getBoundingClientRect = (() =>
                ({
                    left: 0,
                    width: duration * PPS,
                    right: duration * PPS,
                    top: 0,
                    bottom: 20,
                    height: 20,
                    x: 0,
                    y: 0,
                    toJSON: () => ({}),
                }) as DOMRect) as HTMLElement["getBoundingClientRect"];
        }
        track = createTimelineAudioSegmentTrack();
        router = createGestureRouter();
        router.attach(body);
        track.attach(body, router);
        return body;
    };

    beforeEach(() => {
        resetSelectionForTests();
        localStorage.clear();
        saveSpy = jest
            .spyOn(persistence, "saveState")
            .mockImplementation(() => {});
    });

    afterEach(() => {
        track?.dispose();
        router?.dispose();
        track = null;
        router = null;
        jest.restoreAllMocks();
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    it("moves a segment across the whole timeline without changing trim or length", () => {
        const body = setupGlobal();
        const segment = el(body, '.vst-audio-seg[data-track-idx="0"]');

        segment.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 3.5 * PPS));
        document.dispatchEvent(mouse("mouseup", 3.5 * PPS));

        const saved = saveSpy.mock.calls[0][0] as VideoStagesConfig;
        expect(saved.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 3.5,
            timelineLengthSeconds: 3,
            sourceStartSeconds: 1,
        });
        expect(getSelection()).toEqual({ kind: "audio-track", trackIdx: 0 });
    });

    it("clamps movement to the join-adjusted output duration", () => {
        const body = setupGlobal(true, true);
        const segment = el(body, '.vst-audio-seg[data-track-idx="0"]');

        segment.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 5 * PPS));
        document.dispatchEvent(mouse("mouseup", 5 * PPS));

        const saved = saveSpy.mock.calls[0][0] as VideoStagesConfig;
        expect(saved.audioTracks?.[0].spans[0].timelineStartSeconds).toBe(3.1);
    });

    it("snaps to the segment immediately above before clip edges", () => {
        const state = rootState() as unknown as VideoStagesConfig;
        state.audioTracks?.push({
            id: "track-lower",
            volume: 1,
            source: {
                kind: "Upload",
                reference: "",
                uploadedAudio: null,
            },
            spans: [
                {
                    id: "span-lower",
                    timelineStartSeconds: 1,
                    timelineLengthSeconds: 2,
                    sourceStartSeconds: 0,
                },
            ],
        });
        mountVideoStagesData(state);
        mountPromptBox("");
        const body = makeBody();
        body.innerHTML =
            `<div class="vst-audio-seg" data-vst-audio-seg data-track-idx="0"></div>` +
            `<div class="vst-audio-seg" data-vst-audio-seg data-track-idx="1"></div>`;
        track = createTimelineAudioSegmentTrack();
        router = createGestureRouter();
        router.attach(body);
        track.attach(body, router);

        const lower = el(body, '.vst-audio-seg[data-track-idx="1"]');
        lower.dispatchEvent(mouse("mousedown", 1 * PPS));
        document.dispatchEvent(mouse("mousemove", 2.1 * PPS));
        document.dispatchEvent(mouse("mouseup", 2.1 * PPS));

        const saved = saveSpy.mock.calls[0][0] as VideoStagesConfig;
        expect(saved.audioTracks?.[1].spans[0].timelineStartSeconds).toBe(2);
    });

    it("shift+click deletes the whole track behind the last segment", () => {
        const body = setupGlobal();
        const segment = el(body, '.vst-audio-seg[data-track-idx="0"]');

        segment.dispatchEvent(mouse("click", 2 * PPS, true));

        const saved = saveSpy.mock.calls[0][0] as VideoStagesConfig;
        expect(saved.audioTracks).toHaveLength(0);
        expect(getSelection()).toEqual({ kind: "none" });
    });

    it("deleting one of several tracks selects the surviving neighbour", () => {
        const state = rootState() as unknown as VideoStagesConfig;
        state.audioTracks?.push({
            id: "track-lower",
            volume: 1,
            source: { kind: "Upload", reference: "", uploadedAudio: null },
            spans: [
                {
                    id: "span-lower",
                    timelineStartSeconds: 1,
                    timelineLengthSeconds: 2,
                    sourceStartSeconds: 0,
                },
            ],
        });
        mountVideoStagesData(state);
        mountPromptBox("");
        const body = makeBody();
        body.innerHTML =
            `<div class="vst-audio-seg" data-track-idx="0"></div>` +
            `<div class="vst-audio-seg" data-track-idx="1"></div>`;
        track = createTimelineAudioSegmentTrack();
        router = createGestureRouter();
        router.attach(body);
        track.attach(body, router);

        el(body, '.vst-audio-seg[data-track-idx="1"]').dispatchEvent(
            mouse("click", 10, true),
        );

        const saved = saveSpy.mock.calls[0][0] as VideoStagesConfig;
        expect(saved.audioTracks).toHaveLength(1);
        expect(saved.audioTracks?.[0].id).toBe("track-global");
        expect(getSelection()).toEqual({ kind: "audio-track", trackIdx: 0 });
    });

    it("left resize stops at the untrimmed start of the source", () => {
        const body = setupGlobal();
        const left = el(body, '[data-vst-audio-seg-edge="left"]');

        // The segment starts at 2s with a 1s trim, so its source began at 1s.
        left.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 0));
        document.dispatchEvent(mouse("mouseup", 0));

        const span = (saveSpy.mock.calls[0][0] as VideoStagesConfig)
            .audioTracks?.[0].spans[0];
        expect(span).toMatchObject({
            timelineStartSeconds: 1,
            timelineLengthSeconds: 4,
            sourceStartSeconds: 0,
        });
    });

    it("falls back to clip edges and bypasses snapping when disabled", () => {
        let body = setupGlobal();
        let segment = el(body, '.vst-audio-seg[data-track-idx="0"]');
        segment.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 3.1 * PPS));
        document.dispatchEvent(mouse("mouseup", 3.1 * PPS));
        expect(
            (saveSpy.mock.calls[0][0] as VideoStagesConfig).audioTracks?.[0]
                .spans[0].timelineStartSeconds,
        ).toBe(3);

        track?.dispose();
        router?.dispose();
        track = null;
        router = null;
        document.body.innerHTML = "";
        saveSpy.mockClear();
        setTimelineAuthoringSetting("snap", false);
        body = setupGlobal();
        segment = el(body, '.vst-audio-seg[data-track-idx="0"]');
        segment.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 3.1 * PPS));
        document.dispatchEvent(mouse("mouseup", 3.1 * PPS));
        expect(
            (saveSpy.mock.calls[0][0] as VideoStagesConfig).audioTracks?.[0]
                .spans[0].timelineStartSeconds,
        ).toBe(3.1);
    });

    it("left resize advances the source trim while keeping the end fixed", () => {
        const body = setupGlobal();
        const left = el(body, '[data-vst-audio-seg-edge="left"]');

        left.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 3 * PPS));
        document.dispatchEvent(mouse("mouseup", 3 * PPS));

        const span = (saveSpy.mock.calls[0][0] as VideoStagesConfig)
            .audioTracks?.[0].spans[0];
        expect(span).toMatchObject({
            timelineStartSeconds: 3,
            timelineLengthSeconds: 2,
            sourceStartSeconds: 2,
        });
    });

    it("creates a default segment on the global blank lane", () => {
        const body = setupGlobal(false);
        const lane = el(body, "[data-vst-audio-seg-add]");

        lane.dispatchEvent(mouse("mousedown", 4 * PPS));
        document.dispatchEvent(mouse("mouseup", 4 * PPS));

        const saved = saveSpy.mock.calls[0][0] as VideoStagesConfig;
        expect(saved.audioTracks).toHaveLength(1);
        expect(saved.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 4,
            timelineLengthSeconds: 2,
            sourceStartSeconds: 0,
        });
    });

    it("allows independently overlapping global lanes", () => {
        const body = setupGlobal(false);
        const lane = el(body, "[data-vst-audio-seg-add]");

        lane.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 5 * PPS));
        document.dispatchEvent(mouse("mouseup", 5 * PPS));

        const saved = saveSpy.mock.calls[0][0] as VideoStagesConfig;
        expect(saved.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 2,
            timelineLengthSeconds: 3,
        });
    });
});
