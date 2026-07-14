import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { getClips } from "./persistence";
import {
    createTimelineAudioTrack,
    type TimelineAudioTrack,
} from "./timelineAudioTrack";
import { computeRegionLayout, renderAudioTrackRow } from "./timelineView";
import type { Clip } from "./types";
import { getSelection, resetSelectionForTests } from "./uiState";

const PPS = 44;

interface ClipFixture {
    duration: number;
    audioSource?: string;
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    audioSource: clip.audioSource ?? "Native",
    stages: [{}],
    refs: [],
    promptWindows: [],
});

const mountPrompt = (clips: ClipFixture[]): void => {
    mountPromptBox("");
    mountVideoStagesData({ clips: clips.map(clipRecord) });
};

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
};

const renderAudio = (body: HTMLElement, clips: Clip[]): void => {
    const layouts = computeRegionLayout(clips, { pxPerSecond: PPS });
    body.innerHTML = renderAudioTrackRow(clips, layouts);
};

describe("createTimelineAudioTrack (selection wiring)", () => {
    let track: TimelineAudioTrack | null = null;

    beforeEach(() => {
        resetSelectionForTests();
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    const setup = (fixtures: ClipFixture[]): HTMLElement => {
        mountPrompt(fixtures);
        const body = makeBody();
        renderAudio(body, getClips());
        track = createTimelineAudioTrack();
        track.attach(body);
        return body;
    };

    it("selects the clip's audio when its segment is clicked", () => {
        const body = setup([{ duration: 5 }, { duration: 5 }]);
        body.querySelector<HTMLElement>(
            '.vst-audio-clip[data-clip-idx="1"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(getSelection()).toEqual({ kind: "audio", clipIdx: 1 });
    });

    it("selects the audio via keyboard activation", () => {
        const body = setup([{ duration: 5 }]);
        body.querySelector<HTMLElement>(
            '.vst-audio-clip[data-clip-idx="0"]',
        )?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "audio", clipIdx: 0 });
    });
});

describe("renderAudioTrackRow (segment spans)", () => {
    afterEach(() => {
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    it("draws one positioned segment span per clip segment", () => {
        mountPromptBox("");
        mountVideoStagesData({
            clips: [
                {
                    duration: 10,
                    audioSource: "Native",
                    stages: [{}],
                    refs: [],
                    audioSegments: [
                        {
                            source: {
                                data: "data:audio/wav;base64,QUJD",
                                fileName: "a.wav",
                            },
                            startSeconds: 2,
                            trimStartSeconds: 0,
                            lengthSeconds: 4,
                        },
                    ],
                },
            ],
        });
        const clips = getClips();
        const layouts = computeRegionLayout(clips, { pxPerSecond: PPS });
        const html = renderAudioTrackRow(clips, layouts);
        const host = document.createElement("div");
        host.innerHTML = html;

        const segs = host.querySelectorAll(".vst-audio-seg");
        expect(segs).toHaveLength(1);
        const seg = segs[0] as HTMLElement;
        expect(seg.getAttribute("data-clip-idx")).toBe("0");
        expect(seg.getAttribute("data-seg-idx")).toBe("0");
        // start 2/10 = 20%, length 4/10 = 40%.
        expect(seg.style.left).toBe("20%");
        expect(seg.style.width).toBe("40%");
        expect(seg.querySelector(".vst-audio-seg-resize-l")).not.toBeNull();
        expect(seg.querySelector(".vst-audio-seg-resize-r")).not.toBeNull();
    });

    it("renders no segment spans when a clip has none", () => {
        mountPromptBox("");
        mountVideoStagesData({
            clips: [{ duration: 5, stages: [{}], refs: [] }],
        });
        const clips = getClips();
        const layouts = computeRegionLayout(clips, { pxPerSecond: PPS });
        const host = document.createElement("div");
        host.innerHTML = renderAudioTrackRow(clips, layouts);
        expect(host.querySelectorAll(".vst-audio-seg")).toHaveLength(0);
    });
});
