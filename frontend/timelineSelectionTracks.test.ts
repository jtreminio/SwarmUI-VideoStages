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
    TIMELINE_PPS,
} from "./__test_helpers__/dom";
import * as persistence from "./persistence/repository";
import { getSelection, resetSelectionForTests } from "./selection";
import {
    createTimelineSelectionTracks,
    type TimelineSelectionTracks,
} from "./timelineSelectionTracks";
import { computeRegionLayout, type RegionLayout } from "./timelineView/layout";
import { renderBoundarySeams } from "./timelineView/regionRenderer";
import { renderAudioTrackRow } from "./timelineView/trackRows";
import type { Clip } from "./types";

describe("createTimelineSelectionTracks", () => {
    let track: TimelineSelectionTracks | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        resetSelectionForTests();
        persistence.__resetPersistenceForTests();
        saveSpy = jest.spyOn(persistence, "saveClips");
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        jest.restoreAllMocks();
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    const setup = (
        clips: Record<string, unknown>[],
        render: (clips: Clip[], layouts: RegionLayout[]) => string,
    ): HTMLElement => {
        mountPromptBox("");
        mountVideoStagesData({ clips });
        const body = mountTimelineBody();
        const normalized = persistence.getClips();
        body.innerHTML = render(
            normalized,
            computeRegionLayout(normalized, { pxPerSecond: TIMELINE_PPS }),
        );
        track = createTimelineSelectionTracks();
        track.attach(body);
        return body;
    };

    const audioSetup = (count: number): HTMLElement =>
        setup(
            Array.from({ length: count }, () =>
                storedClip({ duration: 5, audioSource: "Native" }),
            ),
            renderAudioTrackRow,
        );

    const boundarySetup = (count: number): HTMLElement =>
        setup(
            Array.from({ length: count }, () =>
                storedClip({ duration: 5, boundaryOut: "cut" }),
            ),
            renderBoundarySeams,
        );

    it("selects the clip's audio when its segment is clicked", () => {
        const body = audioSetup(2);
        body.querySelector<HTMLElement>(
            '.vst-audio-clip[data-clip-idx="1"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(getSelection()).toEqual({ kind: "audio", clipIdx: 1 });
    });

    it("selects the audio via keyboard activation", () => {
        const body = audioSetup(1);
        body.querySelector<HTMLElement>(
            '.vst-audio-clip[data-clip-idx="0"]',
        )?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "audio", clipIdx: 0 });
    });

    it("selects the boundary without mutating or saving on click", () => {
        const body = boundarySetup(2);
        body.querySelector<HTMLElement>(
            '[data-vst-boundary-chip][data-left-clip-idx="0"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(getSelection()).toEqual({ kind: "boundary", leftClipIdx: 0 });
        expect(persistence.getClips()[0].boundaryOut).toBe("cut");
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("selects the boundary via keyboard activation", () => {
        const body = boundarySetup(2);
        body.querySelector<HTMLElement>(
            '[data-vst-boundary-chip][data-left-clip-idx="0"]',
        )?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "boundary", leftClipIdx: 0 });
        expect(saveSpy).not.toHaveBeenCalled();
    });
});
