import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { editorHarness } from "../__test_helpers__/editor";
import { getState } from "../persistence/repository";
import { TIMELINE_AUTHORING_SETTINGS_CHANGED } from "../timelineAuthoringSettings";

describe("editor extension settings", () => {
    const h = editorHarness();
    beforeEach(() => {
        Reflect.deleteProperty(globalThis, "installFeatureById");
        Reflect.deleteProperty(globalThis, "currentBackendFeatureSet");
        h.setup(
            [{ duration: 4, stages: [{}] }],
            [
                "root.safetensors",
                "styles/anime.safetensors",
                "styles/realistic/film.safetensors",
                "motion/camera.safetensors",
            ],
        );
    });
    afterEach(() => {
        Reflect.deleteProperty(globalThis, "installFeatureById");
        Reflect.deleteProperty(globalThis, "currentBackendFeatureSet");
    });
    const required = <T extends HTMLElement>(selector: string): T => {
        const result = h.section("settings").querySelector<T>(selector);
        if (!result) throw new Error(`Missing settings control: ${selector}`);
        return result;
    };

    it("resets VideoStages to an empty editable document and preserves unrelated preferences", () => {
        for (const key of [
            "videostages_ui_state",
            "videostages.timeline.viewState",
            "videostages.timeline.authoringSettings",
            "videostages.detail.openRepeaterItems",
            "videostages_authoring_state_v1",
            "videostages.futureJson",
        ]) {
            localStorage.setItem(key, '{"saved":true}');
        }
        localStorage.setItem("unrelated.preference", '{"keep":true}');
        const prompt =
            document.querySelector<HTMLTextAreaElement>("#input_prompt");
        if (!prompt) throw new Error("Missing global prompt");
        prompt.value = "Global prompt";
        required<HTMLButtonElement>(".vst-reset-videostages").click();
        expect(getState()).toMatchObject({
            clips: [],
            audioTracks: [],
            dimsExplicit: false,
        });
        expect(
            Object.fromEntries(
                Object.keys(localStorage).map((key) => [
                    key,
                    localStorage.getItem(key),
                ]),
            ),
        ).toEqual({ "unrelated.preference": '{"keep":true}' });
        expect(prompt.value).toBe("Global prompt");
        expect(
            h.root.querySelector<HTMLButtonElement>(
                '[data-testid="add-first-clip"]',
            )?.disabled,
        ).toBe(false);
    });

    it("filters top-level LoRA folders and saves their checked selection", () => {
        const options = [
            ...h
                .section("settings")
                .querySelectorAll<HTMLInputElement>(
                    "[data-vst-multi-select-option]",
                ),
        ];
        expect(options.map((input) => [input.value, input.checked])).toEqual([
            ["", true],
            ["motion", true],
            ["styles", true],
        ]);
        required<HTMLButtonElement>("[data-vst-multi-select-trigger]").click();
        const filter = required<HTMLInputElement>(
            "[data-vst-multi-select-filter]",
        );
        filter.value = "mot";
        filter.dispatchEvent(new Event("input", { bubbles: true }));
        expect(
            [
                ...h
                    .section("settings")
                    .querySelectorAll<HTMLElement>(".vst-multi-select-option"),
            ]
                .filter((option) => option.style.display !== "none")
                .map((option) => option.textContent),
        ).toEqual(["motion"]);
        options.find((input) => input.value === "motion")?.click();
        expect(
            JSON.parse(
                localStorage.getItem(
                    "videostages.timeline.authoringSettings",
                ) ?? "{}",
            ),
        ).toMatchObject({ loraFolders: ["", "styles"] });
    });

    it("clears LoRA folders and refreshes authoring settings immediately", () => {
        const changed = jest.fn();
        window.addEventListener(TIMELINE_AUTHORING_SETTINGS_CHANGED, changed);
        try {
            required<HTMLButtonElement>(".vst-multi-select-clear").click();
            expect(
                JSON.parse(
                    localStorage.getItem(
                        "videostages.timeline.authoringSettings",
                    ) ?? "{}",
                ),
            ).toMatchObject({ loraFolders: [] });
            expect(
                [
                    ...h
                        .section("settings")
                        .querySelectorAll<HTMLInputElement>(
                            "[data-vst-multi-select-option]",
                        ),
                ].map((input) => [input.value, input.checked]),
            ).toEqual([
                ["", false],
                ["motion", false],
                ["styles", false],
            ]);
            expect(changed).toHaveBeenCalledTimes(1);
        } finally {
            window.removeEventListener(
                TIMELINE_AUTHORING_SETTINGS_CHANGED,
                changed,
            );
        }
    });

    it("installs Bonita and displays its installed status after refresh", () => {
        const installFeatureById = jest.fn();
        Object.assign(globalThis, { installFeatureById });
        const install = required<HTMLButtonElement>(
            "[data-vst-install-bonita]",
        );
        const repository = required<HTMLAnchorElement>(
            ".vst-bonita-install-setting small a",
        );
        expect(repository.href).toBe(
            "https://github.com/jtreminio/ComfyUI-Bonita",
        );
        expect(repository.rel).toBe("noopener noreferrer");
        expect(install.disabled).toBe(false);
        install.click();
        expect(installFeatureById).toHaveBeenCalledWith(
            "bonita",
            required<HTMLElement>(".vst-bonita-install-setting").id,
        );
        Object.assign(globalThis, {
            currentBackendFeatureSet: [
                "bonita.sam3-finder",
                "bonita.yolo-finder",
                "bonita.find-and-crop",
                "bonita.apply-repair",
            ],
        });
        h.editor.refresh();
        const installed = required<HTMLButtonElement>(
            "[data-vst-install-bonita]",
        );
        expect(installed.textContent).toBe("ComfyUI-Bonita Installed");
        expect(installed.disabled).toBe(true);
    });
});
