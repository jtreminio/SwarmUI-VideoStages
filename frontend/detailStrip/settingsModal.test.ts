import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";

import { storedClip } from "../__test_helpers__/clipFixtures";
import { mountPromptBox, mountVideoStagesData } from "../__test_helpers__/dom";
import {
    __resetPersistenceForTests,
    getState,
} from "../persistence/repository";
import {
    closeTimelineAuthoringSettingsModal,
    openTimelineAuthoringSettingsModal,
} from "./settingsModal";

const VIDEO_STAGES_KEYS = [
    "videostages_ui_state",
    "videostages.timeline.viewState",
    "videostages.timeline.authoringSettings",
    "videostages.detail.openRepeaterItems",
    "videostages_authoring_state_v1",
    "videostages.futureJson",
];

describe("timeline authoring settings modal", () => {
    beforeEach(() => {
        __resetPersistenceForTests();
        localStorage.clear();
        document.body.innerHTML = "";
        mountVideoStagesData({ clips: [storedClip()] });
        mountPromptBox("Global prompt");
        for (const key of VIDEO_STAGES_KEYS) {
            localStorage.setItem(key, '{"saved":true}');
        }
        localStorage.setItem("unrelated.preference", '{"keep":true}');
    });

    afterEach(() => {
        closeTimelineAuthoringSettingsModal();
        __resetPersistenceForTests();
        localStorage.clear();
        document.body.innerHTML = "";
    });

    it("resets all VideoStages data immediately", () => {
        expect(getState().clips).toHaveLength(1);
        openTimelineAuthoringSettingsModal();
        const reset = document.querySelector<HTMLButtonElement>(
            ".vst-reset-videostages",
        );

        expect(reset).not.toBeNull();
        reset?.click();
        const stored = JSON.parse(
            document.querySelector<HTMLTextAreaElement>("#input_videostages")
                ?.value ?? "{}",
        ) as Record<string, unknown>;
        expect(stored.clips).toEqual([]);
        expect(stored.audioTracks).toEqual([]);
        expect(stored).not.toHaveProperty("width");
        expect(stored).not.toHaveProperty("height");
        expect(getState().clips).toEqual([]);
        for (const key of VIDEO_STAGES_KEYS) {
            expect(localStorage.getItem(key)).toBeNull();
        }
        expect(localStorage.getItem("unrelated.preference")).toBe(
            '{"keep":true}',
        );
        expect(
            document.querySelector<HTMLTextAreaElement>("#input_prompt")?.value,
        ).toBe("Global prompt");
        expect(
            document.querySelector(".vst-timeline-settings-modal"),
        ).toBeNull();
    });
});
