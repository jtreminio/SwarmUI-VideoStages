import { describe, expect, it, jest } from "@jest/globals";
import { lastSavedClips } from "../__test_helpers__/dom";
import { editorHarness, modelGlobals } from "../__test_helpers__/editor";
import { setSelection } from "../selection";
import type { Clip } from "../types";

describe("production editor clip LoRA panel", () => {
    const h = editorHarness();

    it("adds and persists a LoRA row", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLElement>(
                ".vst-clip-editor [data-testid='add-loras']",
            )
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(
            document.querySelectorAll(
                ".vst-clip-editor [data-testid='section-loras'] [data-testid='editor-item']",
            ),
        ).toHaveLength(1);
        expect(h.saveSpy).toHaveBeenCalled();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 1 },
        ]);
        expect(
            h.section("loras").querySelector("[data-testid=add-loras]")
                ?.textContent,
        ).toBe("+ Add LoRA");
    });

    it("seeds a new clip LoRA from SwarmUI model metadata", () => {
        modelGlobals.sdLoraBrowser = {
            models: {
                "weighted-lora.safetensors": {
                    data: { lora_default_weight: "0.65" },
                },
            },
        };
        h.setup([{ duration: 4, stages: [{}] }], ["weighted-lora.safetensors"]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>("[data-testid='add-loras']")
            ?.click();

        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].loras[0],
                "weight",
            ),
        ).toBe(0.65);
    });

    it("falls back to SwarmUI's remembered per-LoRA weight", () => {
        modelGlobals.sdLoraBrowser = {
            models: {
                "weighted-lora.safetensors": {
                    data: { lora_default_weight: "" },
                },
            },
        };
        modelGlobals.loraHelper = {
            loraWeightPref: { "weighted-lora.safetensors": "0.55" },
        };
        h.setup([{ duration: 4, stages: [{}] }], ["weighted-lora.safetensors"]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>("[data-testid='add-loras']")
            ?.click();

        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].loras[0],
                "weight",
            ),
        ).toBe(0.55);
    });

    it("renders each LoRA as an editable row in one section", () => {
        h.setup(
            [
                {
                    duration: 4,
                    stages: [
                        {
                            loras: [
                                { name: "lora-x.safetensors", weight: 0.5 },
                            ],
                        },
                    ],
                },
            ],
            ["lora-x.safetensors", "lora-y.safetensors"],
        );
        h.section("loras")
            .querySelector<HTMLButtonElement>("[data-testid=add-loras]")
            ?.click();
        expect(
            h.section("loras").querySelectorAll("[data-testid=editor-item]"),
        ).toHaveLength(2);
        expect(
            h.field<HTMLSelectElement>("loras", "Model", "select", 0).value,
        ).toBe("lora-x.safetensors");
        expect(
            h.field<HTMLSelectElement>("loras", "Model", "select", 1).value,
        ).toBe("lora-y.safetensors");
    });

    it("renders delete, model, and weight controls in each LoRA row", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 0.7 }] },
                ],
            },
        ]);
        const row = h.item("loras", 0);
        expect(
            row.querySelector("[data-testid=remove-item]")?.textContent,
        ).toBe("Delete");
        expect(
            h.field<HTMLSelectElement>("loras", "Model", "select", 0).value,
        ).toBe("lora-x.safetensors");
        const weight = h.field("loras", "Weight", "input", 0);
        expect(weight.value).toBe("0.7");
        expect(weight.step).toBe("0.05");
        expect(weight.classList.contains("lora-weight-input")).toBe(true);
    });

    it("debounces a LoRA weight edit through the keyed pending map", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 1 }] },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const weight = document.querySelector<HTMLInputElement>(
            '.vst-clip-editor input[data-vst-focus-key$="clip-0-lora-0-weight"]',
        );
        if (!weight) {
            throw new Error("lora weight input missing");
        }
        expect(weight.getAttribute("data-vst-focus-key")).toEqual(
            expect.stringMatching(/\/clip-0-lora-0-weight$/),
        );
        weight.value = "0.4";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].loras[0],
                "weight",
            ),
        ).toBe(0.4);
    });

    it("allows a negative LoRA weight through the number input", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 1 }] },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const weight = document.querySelector<HTMLInputElement>(
            '.vst-clip-editor input[data-vst-focus-key$="clip-0-lora-0-weight"]',
        );
        if (!weight) throw new Error("lora weight input missing");
        weight.value = "-2.5";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].loras[0],
                "weight",
            ),
        ).toBe(-2.5);
    });

    it("removes a LoRA row (flush-first) through saveClips", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    {
                        loras: [
                            { name: "lora-x.safetensors", weight: 1 },
                            { name: "lora-y.safetensors", weight: 0.5 },
                        ],
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(
            document.querySelectorAll(
                ".vst-clip-editor [data-testid='section-loras'] [data-testid='editor-item']",
            ),
        ).toHaveLength(2);
        document
            .querySelectorAll<HTMLElement>(
                ".vst-clip-editor [data-testid='section-loras'] [data-testid='remove-item']",
            )[0]
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].loras).toEqual([
            { name: "lora-y.safetensors", weight: 0.5 },
        ]);
        expect(
            document.querySelectorAll(
                ".vst-clip-editor [data-testid='section-loras'] [data-testid='editor-item']",
            ),
        ).toHaveLength(1);
        expect(
            h.section("loras").querySelector("[data-testid=add-loras]")
                ?.textContent,
        ).toBe("+ Add LoRA");
    });

    it("keeps the clip LoRA unchanged when adding a stage", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    {
                        loras: [{ name: "lora-x.safetensors", weight: 0.65 }],
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>("[data-testid='add-stages']")
            ?.click();
        const stages = lastSavedClips<Clip[]>(h.saveSpy)[0].stages;
        expect(stages).toHaveLength(2);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 0.65 },
        ]);
    });
});
