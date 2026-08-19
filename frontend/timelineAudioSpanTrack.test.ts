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
    mountPromptBox,
    mountTimelineBody,
    mountVideoStagesData,
    mouse,
    requireEl,
    stubRect,
    TIMELINE_PPS,
} from "./__test_helpers__/dom";
import { createGestureRouter, type GestureRouter } from "./gestureRouter";
import * as persistence from "./persistence/repository";
import { getSelection, resetSelectionForTests } from "./selection";
import {
    createTimelineAudioSpanTrack,
    type TimelineAudioSpanTrack,
} from "./timelineAudioSpanTrack";
import { setTimelineAuthoringSetting } from "./timelineAuthoringSettings";
import { renderTimeline } from "./timelineView";
import type { AudioTrack, AuthoringDocument } from "./types";

describe("timeline-wide audio span gestures", () => {
    let track: TimelineAudioSpanTrack | null = null;
    let router: GestureRouter | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveState>;

    const rootState = (
        withTrack = true,
        withJoin = false,
    ): Record<string, unknown> => {
        const clips = [
            storedClip({ duration: 3 }),
            storedClip({ duration: 4 }),
        ];
        if (withJoin) {
            Object.assign(clips[0], {
                boundaryOut: "crossfade",
                boundaryOutOverlap: 24,
            });
        }
        return {
            schemaVersion: 8,
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

    const LOWER_TRACK: AudioTrack = {
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
    };

    const setupGlobal = (
        withTrack = true,
        withJoin = false,
        extraTracks: AudioTrack[] = [],
    ): HTMLElement => {
        const state = rootState(withTrack, withJoin);
        (state.audioTracks as AudioTrack[]).push(...extraTracks);
        mountVideoStagesData(state);
        mountPromptBox("");
        const duration = withJoin ? 6 : 7;
        const body = mountTimelineBody();
        renderTimeline(body, persistence.getClips(), {
            pxPerSecond: TIMELINE_PPS,
            audioTracks: persistence.getState().audioTracks,
        });
        for (const lane of body.querySelectorAll<HTMLElement>(
            ".vst-audio-track-lane",
        )) {
            stubRect(lane, 0, duration * TIMELINE_PPS);
        }
        track = createTimelineAudioSpanTrack();
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
        const segment = requireEl(body, '.vst-audio-span[data-track-idx="0"]');

        segment.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 3.5 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 3.5 * TIMELINE_PPS));

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 3.5,
            timelineLengthSeconds: 3,
            sourceStartSeconds: 1,
        });
        expect(getSelection()).toEqual({ kind: "audio-track", trackIdx: 0 });
    });

    it("clamps movement to the join-adjusted output duration", () => {
        const body = setupGlobal(true, true);
        const segment = requireEl(body, '.vst-audio-span[data-track-idx="0"]');

        segment.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 5 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 5 * TIMELINE_PPS));

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks?.[0].spans[0].timelineStartSeconds).toBe(3.1);
    });

    it("snaps to the span immediately above before clip edges", () => {
        const body = setupGlobal(true, false, [LOWER_TRACK]);

        const lower = requireEl(body, '.vst-audio-span[data-track-idx="1"]');
        lower.dispatchEvent(mouse("mousedown", 1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 2.1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 2.1 * TIMELINE_PPS));

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks?.[1].spans[0].timelineStartSeconds).toBe(2);
    });

    it("shift+click deletes the whole track behind the last segment", () => {
        const body = setupGlobal();
        const segment = requireEl(body, '.vst-audio-span[data-track-idx="0"]');

        segment.dispatchEvent(
            mouse("click", 2 * TIMELINE_PPS, { shiftKey: true }),
        );

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks).toHaveLength(0);
        expect(getSelection()).toEqual({ kind: "none" });
    });

    it("deleting one of several tracks selects the surviving neighbour", () => {
        const body = setupGlobal(true, false, [LOWER_TRACK]);

        requireEl(body, '.vst-audio-span[data-track-idx="1"]').dispatchEvent(
            mouse("click", 10, { shiftKey: true }),
        );

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks).toHaveLength(1);
        expect(saved.audioTracks?.[0].id).toBe("track-global");
        expect(getSelection()).toEqual({ kind: "audio-track", trackIdx: 0 });
    });

    it("left resize stops at the untrimmed start of the source", () => {
        const body = setupGlobal();
        const left = requireEl(body, '[data-vst-audio-span-edge="left"]');

        // The segment starts at 2s with a 1s trim, so its source began at 1s.
        left.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 0));
        document.dispatchEvent(mouse("mouseup", 0));

        const span = (saveSpy.mock.calls[0][0] as AuthoringDocument)
            .audioTracks?.[0].spans[0];
        expect(span).toMatchObject({
            timelineStartSeconds: 1,
            timelineLengthSeconds: 4,
            sourceStartSeconds: 0,
        });
    });

    it("falls back to clip edges and bypasses snapping when disabled", () => {
        let body = setupGlobal();
        let segment = requireEl(body, '.vst-audio-span[data-track-idx="0"]');
        segment.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 3.1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 3.1 * TIMELINE_PPS));
        expect(
            (saveSpy.mock.calls[0][0] as AuthoringDocument).audioTracks?.[0]
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
        segment = requireEl(body, '.vst-audio-span[data-track-idx="0"]');
        segment.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 3.1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 3.1 * TIMELINE_PPS));
        expect(
            (saveSpy.mock.calls[0][0] as AuthoringDocument).audioTracks?.[0]
                .spans[0].timelineStartSeconds,
        ).toBe(3.1);
    });

    it("left resize advances the source trim while keeping the end fixed", () => {
        const body = setupGlobal();
        const left = requireEl(body, '[data-vst-audio-span-edge="left"]');

        left.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 3 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 3 * TIMELINE_PPS));

        const span = (saveSpy.mock.calls[0][0] as AuthoringDocument)
            .audioTracks?.[0].spans[0];
        expect(span).toMatchObject({
            timelineStartSeconds: 3,
            timelineLengthSeconds: 2,
            sourceStartSeconds: 2,
        });
    });

    it("creates a default span on the global blank lane", () => {
        const body = setupGlobal(false);
        const lane = requireEl(
            body,
            ".vst-audio-track-lane[data-vst-audio-track-add]",
        );

        lane.dispatchEvent(mouse("mousedown", 4 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 4 * TIMELINE_PPS));

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks).toHaveLength(1);
        expect(saved.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 4,
            timelineLengthSeconds: 2,
            sourceStartSeconds: 0,
        });
    });

    const headAddButton = (body: HTMLElement): HTMLElement =>
        requireEl(body, ".vst-head-tag-track[data-vst-audio-track-add]");

    it("adds a track from the track head's add button", () => {
        const body = setupGlobal(false);

        headAddButton(body).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks).toHaveLength(1);
        // The button carries no time of its own, so the track starts the timeline.
        expect(saved.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 0,
            timelineLengthSeconds: 2,
            sourceStartSeconds: 0,
        });
        expect(getSelection()).toEqual({ kind: "audio-track", trackIdx: 0 });
    });

    it("adds a track from the head button on keyboard activation", () => {
        const body = setupGlobal(true);

        headAddButton(body).dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks).toHaveLength(2);
        expect(getSelection()).toEqual({ kind: "audio-track", trackIdx: 1 });
    });

    it("allows independently overlapping global lanes", () => {
        const body = setupGlobal(false);
        const lane = requireEl(
            body,
            ".vst-audio-track-lane[data-vst-audio-track-add]",
        );

        lane.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 5 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 5 * TIMELINE_PPS));

        const saved = saveSpy.mock.calls[0][0] as AuthoringDocument;
        expect(saved.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 2,
            timelineLengthSeconds: 3,
        });
    });
});
