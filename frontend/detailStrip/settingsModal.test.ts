import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";

import { storedClip } from "../__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "../__test_helpers__/dom";
import {
    __resetPersistenceForTests,
    getState,
} from "../persistence/repository";
import { TIMELINE_AUTHORING_SETTINGS_CHANGED } from "../timelineAuthoringSettings";
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
        mountSelect("input_loras", {
            options: [
                "root.safetensors",
                "styles/anime.safetensors",
                "styles/realistic/film.safetensors",
                "motion/camera.safetensors",
            ],
        });
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

    it("selects the top-level folders included in LoRA dropdowns", () => {
        openTimelineAuthoringSettingsModal();
        const folders = Array.from(
            document.querySelectorAll<HTMLInputElement>(
                ".vst-lora-folder-option input[type='checkbox']",
            ),
        );

        expect(folders.map((input) => input.value)).toEqual([
            "",
            "motion",
            "styles",
        ]);
        expect(folders.every((input) => input.checked)).toBe(true);

        folders.find((input) => input.value === "motion")?.click();
        expect(
            JSON.parse(
                localStorage.getItem(
                    "videostages.timeline.authoringSettings",
                ) ?? "{}",
            ),
        ).toMatchObject({ loraFolders: ["", "styles"] });
    });

    it("can clear the folder selection and refreshes after the modal closes", () => {
        const changed = jest.fn();
        window.addEventListener(TIMELINE_AUTHORING_SETTINGS_CHANGED, changed);
        openTimelineAuthoringSettingsModal();

        document
            .querySelector<HTMLButtonElement>(".vst-lora-folders-none")
            ?.click();
        const folders = Array.from(
            document.querySelectorAll<HTMLInputElement>(
                ".vst-lora-folder-option input[type='checkbox']",
            ),
        );
        expect(folders.every((input) => !input.checked)).toBe(true);
        expect(
            JSON.parse(
                localStorage.getItem(
                    "videostages.timeline.authoringSettings",
                ) ?? "{}",
            ),
        ).toMatchObject({ loraFolders: [] });
        expect(changed).not.toHaveBeenCalled();

        document
            .querySelector<HTMLButtonElement>(".modal-header button")
            ?.click();
        expect(changed).toHaveBeenCalledTimes(1);
        window.removeEventListener(
            TIMELINE_AUTHORING_SETTINGS_CHANGED,
            changed,
        );
    });
});
