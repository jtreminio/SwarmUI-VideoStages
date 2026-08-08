import { afterEach, describe, expect, it } from "@jest/globals";
import { storedClip } from "./__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountVideoStagesData,
    TIMELINE_PPS,
} from "./__test_helpers__/dom";
import { getClips } from "./persistence/repository";
import { resetSelectionForTests } from "./selection";
import { computeRegionLayout } from "./timelineView/layout";
import { renderAudioTrackRow } from "./timelineView/trackRows";

describe("renderAudioTrackRow (timeline audio lanes)", () => {
    afterEach(() => {
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    it("renders no timeline audio lanes when no track exists", () => {
        mountPromptBox("");
        mountVideoStagesData({ clips: [storedClip({ duration: 5 })] });
        const clips = getClips();
        const layouts = computeRegionLayout(clips, {
            pxPerSecond: TIMELINE_PPS,
        });
        const host = document.createElement("div");
        host.innerHTML = renderAudioTrackRow(clips, layouts);
        expect(host.querySelectorAll(".vst-audio-span")).toHaveLength(0);
    });
});
