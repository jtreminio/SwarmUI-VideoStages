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
import type { AudioSegment, Clip, VideoStagesConfig } from "./types";

const PPS = 44;

interface ClipFixture {
    duration: number;
    audioSegments?: AudioSegment[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    stages: [{}],
    refs: [],
    ...(clip.audioSegments ? { audioSegments: clip.audioSegments } : {}),
});

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
};

// Minimal audio cell mirroring the real markup: per-clip audio clip on top,
// then one mini lane PER segment plus the blank add-lane at the bottom (only
// the blank lane carries data-vst-audio-seg-add + data-clip-idx).
const renderSegments = (body: HTMLElement, clips: ClipFixture[]): void => {
    let cursor = 0;
    const parts: string[] = [];
    clips.forEach((clip, i) => {
        const startPx = cursor * PPS;
        const widthPx = clip.duration * PPS;
        const segLanes = (clip.audioSegments ?? [])
            .map((seg, s) => {
                const leftPct = (seg.startSeconds / clip.duration) * 100;
                const widthPct = (seg.lengthSeconds / clip.duration) * 100;
                return (
                    `<div class="vst-audio-seg-lane" style="left:${startPx}px;width:${widthPx}px">` +
                    `<div class="vst-audio-seg" data-vst-audio-seg data-clip-idx="${i}" data-seg-idx="${s}" style="left:${leftPct}%;width:${widthPct}%" role="button" tabindex="0">` +
                    `<span class="vst-audio-seg-resize vst-audio-seg-resize-l" data-vst-audio-seg-edge="left"></span>` +
                    `<span class="vst-audio-label"></span>` +
                    `<span class="vst-audio-seg-resize vst-audio-seg-resize-r" data-vst-audio-seg-edge="right"></span>` +
                    `</div></div>`
                );
            })
            .join("");
        parts.push(
            `<div class="vst-audio-clip" data-vst-audio="clip" data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px"></div>`,
            segLanes,
            `<div class="vst-audio-seg-lane vst-audio-seg-lane-blank" data-vst-audio-seg-add data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px"></div>`,
        );
        cursor += clip.duration;
    });
    body.innerHTML =
        `<div class="vst-track-row vst-track-audio"><div class="vst-track-cell vst-audio-cell">` +
        parts.join("") +
        `</div></div>`;
    // jsdom does no layout — stub each lane's rect from its clip offset.
    cursor = 0;
    clips.forEach((clip, i) => {
        const left = cursor * PPS;
        const lane = body.querySelector<HTMLElement>(
            `.vst-audio-seg-lane[data-clip-idx="${i}"]`,
        );
        if (lane) {
            lane.getBoundingClientRect = (() =>
                ({
                    left,
                    width: clip.duration * PPS,
                    right: left + clip.duration * PPS,
                    top: 40,
                    bottom: 60,
                    height: 20,
                    x: left,
                    y: 40,
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
        clientY: 12,
        button: 0,
        shiftKey,
    });

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[0][0] as Clip[];

const savedSegments = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
    clipIdx = 0,
): AudioSegment[] => savedClips(spy)[clipIdx].audioSegments;

const SOURCE = { data: "data:audio/wav;base64,QUJD", fileName: "a.wav" };
const SEGMENT: AudioSegment = {
    source: SOURCE,
    startSeconds: 2,
    trimStartSeconds: 1,
    lengthSeconds: 3,
    volume: 1,
};

describe("createTimelineAudioSegmentTrack (DOM gestures)", () => {
    let track: TimelineAudioSegmentTrack | null = null;
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
        renderSegments(body, clips);
        track = createTimelineAudioSegmentTrack();
        router = createGestureRouter();
        router.attach(body);
        track.attach(body, router);
        return body;
    };

    const oneSegment = (): HTMLElement =>
        setup([{ duration: 10, audioSegments: [SEGMENT] }]);

    it("clicking a segment selects it without saving", () => {
        const body = oneSegment();
        const seg = el(body, ".vst-audio-seg[data-seg-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mouseup", 200));
        seg.dispatchEvent(mouse("click", 200));

        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 0,
        });
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("shift+click on a segment deletes it", () => {
        const body = oneSegment();
        const seg = el(body, ".vst-audio-seg[data-seg-idx='0']");
        seg.dispatchEvent(mouse("click", 200, true));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedSegments(saveSpy)).toEqual([]);
    });

    it("dragging the body moves the segment and keeps trim/length", () => {
        const body = oneSegment();
        const seg = el(body, ".vst-audio-seg[data-seg-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 2 * PPS));

        const s = savedSegments(saveSpy)[0];
        expect(s.startSeconds).toBeCloseTo(4, 5);
        expect(s.lengthSeconds).toBeCloseTo(3, 5);
        expect(s.trimStartSeconds).toBeCloseTo(1, 5);
        // The committed drag selects the dragged segment.
        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 0,
        });
    });

    it("moving past the clip end clamps start so the segment stays inside", () => {
        const body = oneSegment();
        const seg = el(body, ".vst-audio-seg[data-seg-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 20 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 20 * PPS));

        const s = savedSegments(saveSpy)[0];
        expect(s.startSeconds).toBeCloseTo(7, 5); // 10 - length(3)
        expect(s.lengthSeconds).toBeCloseTo(3, 5);
    });

    it("resizing the right edge changes length and keeps start/trim", () => {
        const body = oneSegment();
        const edge = el(body, ".vst-audio-seg-resize-r");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 2 * PPS));

        const s = savedSegments(saveSpy)[0];
        expect(s.startSeconds).toBeCloseTo(2, 5);
        expect(s.lengthSeconds).toBeCloseTo(5, 5);
        expect(s.trimStartSeconds).toBeCloseTo(1, 5);
    });

    it("resizing the left edge trims the head and keeps the end fixed", () => {
        const body = oneSegment();
        const edge = el(body, ".vst-audio-seg-resize-l");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 1 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 1 * PPS));

        const s = savedSegments(saveSpy)[0];
        // start 2 -> 3, trim 1 -> 2, end stays 5 so length 2.
        expect(s.startSeconds).toBeCloseTo(3, 5);
        expect(s.trimStartSeconds).toBeCloseTo(2, 5);
        expect(s.lengthSeconds).toBeCloseTo(2, 5);
    });

    it("dragging the left edge left starts the segment earlier; trim floors at 0", () => {
        const body = oneSegment();
        const edge = el(body, ".vst-audio-seg-resize-l");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 - 3 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 - 3 * PPS));

        const s = savedSegments(saveSpy)[0];
        // Like a relay begin edge: start 2 → -1 clamps to the lane start (0);
        // trim un-winds 1 → 0 and floors there; end stays 5 so length 5.
        expect(s.startSeconds).toBeCloseTo(0, 5);
        expect(s.trimStartSeconds).toBeCloseTo(0, 5);
        expect(s.lengthSeconds).toBeCloseTo(5, 5);
    });

    it("left-edge drag left crosses a neighbouring segment (overlap allowed), clamping at 0", () => {
        const body = setup([
            {
                duration: 20,
                audioSegments: [
                    { ...SEGMENT, startSeconds: 1, trimStartSeconds: 0 }, // A: [1,4]
                    { ...SEGMENT, startSeconds: 8, trimStartSeconds: 0 }, // B: [8,11]
                ],
            },
        ]);
        const edge = el(
            body,
            ".vst-audio-seg[data-seg-idx='1'] .vst-audio-seg-resize-l",
        );
        edge.dispatchEvent(mouse("mousedown", 400));
        document.dispatchEvent(mouse("mousemove", 400 - 10 * PPS));
        document.dispatchEvent(mouse("mouseup", 400 - 10 * PPS));

        const b = savedSegments(saveSpy)[1];
        // Each segment has its own lane: B extends straight over A's span,
        // clamped only at the clip start; end stays 11.
        expect(b.startSeconds).toBeCloseTo(0, 5);
        expect(b.lengthSeconds).toBeCloseTo(11, 5);
    });

    it("Enter on a segment selects it", () => {
        const body = oneSegment();
        const seg = el(body, ".vst-audio-seg[data-seg-idx='0']");
        seg.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );

        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 0,
        });
    });

    it("clicking empty lane space adds a default-length segment and selects it", () => {
        const body = setup([{ duration: 10 }]);
        const lane = el(body, ".vst-audio-seg-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 3 * PPS)); // t = 3s
        document.dispatchEvent(mouse("mouseup", 3 * PPS));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const segs = savedSegments(saveSpy);
        expect(segs).toHaveLength(1);
        expect(segs[0].startSeconds).toBeCloseTo(3, 5);
        expect(segs[0].lengthSeconds).toBeCloseTo(2, 5); // default length
        expect(segs[0].trimStartSeconds).toBe(0);
        expect(segs[0].source).toBeNull();
        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 0,
        });
    });

    it("click-dragging on empty lane space adds a segment sized to the drag", () => {
        const body = setup([{ duration: 10 }]);
        const lane = el(body, ".vst-audio-seg-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 2 * PPS));
        document.dispatchEvent(mouse("mousemove", 6 * PPS));
        document.dispatchEvent(mouse("mouseup", 6 * PPS));

        const segs = savedSegments(saveSpy);
        expect(segs).toHaveLength(1);
        expect(segs[0].startSeconds).toBeCloseTo(2, 5);
        expect(segs[0].lengthSeconds).toBeCloseTo(4, 5);
    });

    it("a segment created before an existing one is APPENDED (index = lane) and selected", () => {
        const body = setup([
            {
                duration: 10,
                audioSegments: [{ ...SEGMENT, startSeconds: 6 }],
            },
        ]);
        const lane = el(body, ".vst-audio-seg-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 1 * PPS)); // t = 1s, before 6s
        document.dispatchEvent(mouse("mouseup", 1 * PPS));

        const segs = savedSegments(saveSpy);
        expect(segs).toHaveLength(2);
        // No start-time sort: the existing segment keeps lane 0, the new one
        // takes the blank lane (appended) and is selected there.
        expect(segs[0].startSeconds).toBeCloseTo(6, 5);
        expect(segs[1].startSeconds).toBeCloseTo(1, 5);
        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 1,
        });
    });

    it("a new segment may overlap an existing one (lanes mix additively)", () => {
        const body = setup([
            {
                duration: 10,
                audioSegments: [{ ...SEGMENT, startSeconds: 4 }], // [4,7]
            },
        ]);
        const lane = el(body, ".vst-audio-seg-lane[data-clip-idx='0']");
        // Tap at 3s: default length 2 reaches 5s, overlapping [4,7] — allowed.
        lane.dispatchEvent(mouse("mousedown", 3 * PPS));
        document.dispatchEvent(mouse("mouseup", 3 * PPS));

        const segs = savedSegments(saveSpy);
        expect(segs).toHaveLength(2);
        expect(segs[1].startSeconds).toBeCloseTo(3, 5);
        expect(segs[1].lengthSeconds).toBeCloseTo(2, 5);
    });

    it("moving a segment keeps its length within the clip bounds", () => {
        const body = setup([
            {
                duration: 20,
                audioSegments: [
                    { ...SEGMENT, startSeconds: 1 }, // A: [1,4]
                    { ...SEGMENT, startSeconds: 10 }, // B: [10,13]
                ],
            },
        ]);
        // Drag A far right, past B's left edge.
        const seg = el(body, ".vst-audio-seg[data-seg-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 15 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 15 * PPS));

        const segs = savedSegments(saveSpy);
        const [a, b] = [...segs].sort(
            (x, y) => x.startSeconds - y.startSeconds,
        );
        expect(a.startSeconds + a.lengthSeconds).toBeLessThanOrEqual(
            b.startSeconds + 1e-6,
        );
        expect(a.lengthSeconds).toBeCloseTo(3, 5);
    });

    it("right-edge resize extends past the next segment (overlap allowed)", () => {
        const body = setup([
            {
                duration: 20,
                audioSegments: [
                    { ...SEGMENT, startSeconds: 2 }, // A: [2,5]
                    { ...SEGMENT, startSeconds: 8 }, // B: [8,11]
                ],
            },
        ]);
        const edge = el(
            body,
            ".vst-audio-seg[data-seg-idx='0'] .vst-audio-seg-resize-r",
        );
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 10 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 10 * PPS));

        const s = savedSegments(saveSpy)[0];
        expect(s.startSeconds).toBeCloseTo(2, 5);
        // Overlap allowed: the end extends straight past B ([8,11]) to 15s.
        expect(s.lengthSeconds).toBeCloseTo(13, 5);
    });
});

describe("timeline-wide audio segment gestures", () => {
    let track: TimelineAudioSegmentTrack | null = null;
    let router: GestureRouter | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveState>;

    const rootState = (withTrack = true): Record<string, unknown> => ({
        schemaVersion: 4,
        clips: [clipRecord({ duration: 3 }), clipRecord({ duration: 4 })],
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
                              firstClipId: null,
                              lastClipId: null,
                              timelineStartSeconds: 2,
                              timelineLengthSeconds: 3,
                              sourceStartSeconds: 1,
                              clipStartOffsetSeconds: null,
                              clipLengthSeconds: null,
                          },
                      ],
                  },
              ]
            : [],
    });

    const setupGlobal = (withTrack = true): HTMLElement => {
        mountVideoStagesData(rootState(withTrack));
        mountPromptBox("");
        const body = makeBody();
        body.innerHTML =
            `<div class="vst-audio-seg-lane${withTrack ? "" : " vst-audio-seg-lane-blank"}" ` +
            `${withTrack ? 'data-track-idx="0"' : "data-vst-audio-seg-add"} style="left:0;width:${7 * PPS}px">` +
            (withTrack
                ? `<div class="vst-audio-seg" data-vst-audio-seg data-track-idx="0" style="left:${(2 / 7) * 100}%;width:${(3 / 7) * 100}%">` +
                  `<span data-vst-audio-seg-edge="left"></span><span data-vst-audio-seg-edge="right"></span></div>`
                : "") +
            `</div>`;
        const lane = body.querySelector<HTMLElement>(".vst-audio-seg-lane");
        if (lane) {
            lane.getBoundingClientRect = (() =>
                ({
                    left: 0,
                    width: 7 * PPS,
                    right: 7 * PPS,
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
