import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { getClips } from "./persistence/repository";
import { getSelection, resetSelectionForTests } from "./selection";
import {
    createTimelineSelectionTracks,
    type TimelineSelectionTracks,
} from "./timelineSelectionTracks";
import { computeRegionLayout } from "./timelineView/layout";
import { renderAudioTrackRow } from "./timelineView/trackRows";
import type { Clip } from "./types";

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

describe("timeline audio selection wiring", () => {
    let track: TimelineSelectionTracks | null = null;

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
        track = createTimelineSelectionTracks();
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

describe("renderAudioTrackRow (timeline audio lanes)", () => {
    afterEach(() => {
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    it("renders no timeline audio lanes when no track exists", () => {
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
