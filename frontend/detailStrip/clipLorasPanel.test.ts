import { describe, expect, it, jest } from "@jest/globals";
import {
    detailStripHarness,
    modelGlobals,
} from "../__test_helpers__/detailStrip";
import { lastSavedClips } from "../__test_helpers__/dom";
import { setSelection } from "../selection";
import type { Clip } from "../types";

describe("detail strip clip LoRA panel", () => {
    const h = detailStripHarness();

    it("adds and persists a LoRA row", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLElement>(".vst-detail .vst-detail-add-lora")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(
            document.querySelectorAll(".vst-detail .vst-clip-lora-entry"),
        ).toHaveLength(1);
        expect(h.saveSpy).toHaveBeenCalled();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 1 },
        ]);
        expect(
            document
                .querySelector(".vst-detail .vst-detail-loras-section")
                ?.classList.contains("input-group-open"),
        ).toBe(true);
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
            .querySelector<HTMLButtonElement>(".vst-detail-add-lora")
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
            .querySelector<HTMLButtonElement>(".vst-detail-add-lora")
            ?.click();

        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].loras[0],
                "weight",
            ),
        ).toBe(0.55);
    });

    it("renders LoRAs as flat rows without per-row minify controls", () => {
        h.setup(
            [
                {
                    duration: 4,
                    stages: [
                        {
                            loras: [
                                {
                                    name: "lora-x.safetensors",
                                    weight: 0.5,
                                },
                            ],
                        },
                    ],
                },
            ],
            ["lora-x.safetensors", "lora-y.safetensors"],
        );
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-lora")
            ?.click();
        const groups = document.querySelectorAll<HTMLElement>(
            ".vst-clip-lora-entry",
        );
        expect(groups).toHaveLength(2);
        expect(groups[0].querySelector(".auto-symbol")).toBeNull();
        expect(groups[1].querySelector(".auto-symbol")).toBeNull();
        expect(groups[0].classList.contains("input-group")).toBe(false);
    });

    it("renders delete, model, and clip weight in one row", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 0.7 }] },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const row = document.querySelector<HTMLElement>(
            ".vst-detail .vst-clip-lora-entry",
        );
        expect(row).not.toBeNull();
        const nameSelect = row?.querySelector<HTMLSelectElement>("select");
        expect(nameSelect?.value).toBe("lora-x.safetensors");
        // Name renders at input font size (via .vst-audio-select), not the
        // small 3xs label size.
        expect(nameSelect?.classList.contains("vst-audio-select")).toBe(true);
        const weight = row?.querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="clip-0-lora-0-weight"]',
        );
        expect(weight?.value).toBe("0.7");
        expect(weight?.step).toBe("0.05");
        expect(weight?.hasAttribute("min")).toBe(false);
        expect(weight?.hasAttribute("max")).toBe(false);
        expect(row?.querySelector("input.auto-slider-range")).toBeNull();
        expect(weight?.classList.contains("lora-weight-input")).toBe(true);
        expect(row?.querySelector("label")).toBeNull();
        const controls = Array.from(row?.children ?? []);
        expect(controls[0]?.classList.contains("vst-detail-delete-lora")).toBe(
            true,
        );
        expect(controls[1]).toBe(nameSelect);
        expect(controls[2]).toBe(weight);
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
            '.vst-detail input[data-vst-focus-key="clip-0-lora-0-weight"]',
        );
        if (!weight) {
            throw new Error("lora weight input missing");
        }
        expect(weight.getAttribute("data-vst-focus-key")).toBe(
            "clip-0-lora-0-weight",
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
            '.vst-detail input[data-vst-focus-key="clip-0-lora-0-weight"]',
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
            document.querySelectorAll(".vst-detail .vst-clip-lora-entry"),
        ).toHaveLength(2);
        document
            .querySelectorAll<HTMLElement>(
                ".vst-detail .vst-detail-delete-lora",
            )[0]
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].loras).toEqual([
            { name: "lora-y.safetensors", weight: 0.5 },
        ]);
        expect(
            document.querySelectorAll(".vst-detail .vst-clip-lora-entry"),
        ).toHaveLength(1);
        expect(
            document
                .querySelector(".vst-detail .vst-detail-loras-section")
                ?.classList.contains("input-group-open"),
        ).toBe(true);
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
            .querySelector<HTMLButtonElement>(".vst-detail-add-stage")
            ?.click();
        const stages = lastSavedClips<Clip[]>(h.saveSpy)[0].stages;
        expect(stages).toHaveLength(2);
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 0.65 },
        ]);
        expect(stages[1]).not.toHaveProperty("loraWeights");
    });
});
