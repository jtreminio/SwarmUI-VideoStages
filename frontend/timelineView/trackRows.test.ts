import { afterEach, describe, expect, it } from "@jest/globals";
import { storedClip } from "../__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountVideoStagesData,
    TIMELINE_PPS,
} from "../__test_helpers__/dom";
import { getClips } from "../persistence/repository";
import { resetSelectionForTests } from "../selection";
import { computeRegionLayout } from "./layout";
import { renderAudioTrackRow } from "./trackRows";

describe("renderAudioTrackRow (timeline audio lanes)", () => {
    afterEach(() => {
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    it("renders the add lane alone when there is no timeline audio track", () => {
        mountPromptBox("");
        mountVideoStagesData({ clips: [storedClip({ duration: 5 })] });
        const clips = getClips();
        const layouts = computeRegionLayout(clips, {
            pxPerSecond: TIMELINE_PPS,
        });
        const host = document.createElement("div");
        host.innerHTML = renderAudioTrackRow(clips, layouts, undefined, []);
        const lanes = host.querySelectorAll(".vst-audio-track-lane");
        expect(lanes).toHaveLength(1);
        expect(lanes[0].hasAttribute("data-vst-audio-track-add")).toBe(true);
        expect(host.querySelectorAll(".vst-audio-span")).toHaveLength(0);
    });
});
