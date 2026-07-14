import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import * as persistence from "./persistence";
import {
    createTimelineAudioSegmentTrack,
    type TimelineAudioSegmentTrack,
} from "./timelineAudioSegmentTrack";
import type { AudioSegment, Clip } from "./types";
import { getSelection, resetSelectionForTests } from "./uiState";

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

// Minimal audio cell + segment overlay with the data-* hooks the module reads.
const renderSegments = (body: HTMLElement, clips: ClipFixture[]): void => {
    let cursor = 0;
    const parts: string[] = [];
    clips.forEach((clip, i) => {
        const startPx = cursor * PPS;
        const widthPx = clip.duration * PPS;
        const segs = (clip.audioSegments ?? [])
            .map((seg, s) => {
                const leftPct = (seg.startSeconds / clip.duration) * 100;
                const widthPct = (seg.lengthSeconds / clip.duration) * 100;
                return (
                    `<div class="vst-audio-seg" data-vst-audio-seg data-clip-idx="${i}" data-seg-idx="${s}" style="left:${leftPct}%;width:${widthPct}%" role="button" tabindex="0">` +
                    `<span class="vst-audio-seg-resize vst-audio-seg-resize-l" data-vst-audio-seg-edge="left"></span>` +
                    `<span class="vst-audio-seg-label"></span>` +
                    `<span class="vst-audio-seg-resize vst-audio-seg-resize-r" data-vst-audio-seg-edge="right"></span>` +
                    `</div>`
                );
            })
            .join("");
        parts.push(
            `<div class="vst-audio-clip" data-vst-audio="clip" data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px">${segs}</div>`,
        );
        cursor += clip.duration;
    });
    body.innerHTML =
        `<div class="vst-track-row vst-track-audio"><div class="vst-track-cell">` +
        parts.join("") +
        `</div></div>`;
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
};

describe("createTimelineAudioSegmentTrack (DOM gestures)", () => {
    let track: TimelineAudioSegmentTrack | null = null;
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
        track.attach(body);
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
        document.dispatchEvent(mouse("mousemove", 100 + 2 * PPS)); // +2s
        document.dispatchEvent(mouse("mouseup", 100 + 2 * PPS));

        const s = savedSegments(saveSpy)[0];
        expect(s.startSeconds).toBeCloseTo(4, 5);
        expect(s.lengthSeconds).toBeCloseTo(3, 5);
        expect(s.trimStartSeconds).toBeCloseTo(1, 5);
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
        document.dispatchEvent(mouse("mousemove", 100 + 2 * PPS)); // +2s
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
        document.dispatchEvent(mouse("mousemove", 100 + 1 * PPS)); // +1s
        document.dispatchEvent(mouse("mouseup", 100 + 1 * PPS));

        const s = savedSegments(saveSpy)[0];
        // start 2 -> 3, trim 1 -> 2, end stays 5 so length 2.
        expect(s.startSeconds).toBeCloseTo(3, 5);
        expect(s.trimStartSeconds).toBeCloseTo(2, 5);
        expect(s.lengthSeconds).toBeCloseTo(2, 5);
    });

    it("dragging the left edge left is clamped so trim never goes below 0", () => {
        const body = oneSegment();
        const edge = el(body, ".vst-audio-seg-resize-l");
        edge.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 - 3 * PPS)); // -3s
        document.dispatchEvent(mouse("mouseup", 100 - 3 * PPS));

        const s = savedSegments(saveSpy)[0];
        // start floored to startStart - trim = 2 - 1 = 1, trim -> 0, end 5 so length 4.
        expect(s.startSeconds).toBeCloseTo(1, 5);
        expect(s.trimStartSeconds).toBeCloseTo(0, 5);
        expect(s.lengthSeconds).toBeCloseTo(4, 5);
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
});
