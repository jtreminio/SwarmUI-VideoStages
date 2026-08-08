import { describe, expect, it, jest } from "@jest/globals";
import {
    detailBody,
    detailStripHarness,
    fieldByLabel,
    modelGlobals,
} from "../__test_helpers__/detailStrip";
import { lastSavedClips } from "../__test_helpers__/dom";
import { setSelection } from "../selection";
import type { Clip } from "../types";

describe("detail strip clip LoRA panel", () => {
    const h = detailStripHarness();
    const { setup } = h;

    it("adds and persists a LoRA row", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLElement>(
                ".vst-detail .vst-detail-loras-section > .input-group-header",
            )
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        document
            .querySelector<HTMLElement>(".vst-detail .vst-detail-add-lora")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(
            document.querySelectorAll(".vst-detail .vst-clip-lora-entry"),
        ).toHaveLength(1);
        expect(h.saveSpy).toHaveBeenCalled();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].loras).toEqual([
            { name: "lora-x.safetensors" },
        ]);
        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loraWeights,
        ).toEqual([1]);
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
        setup([{ duration: 4, stages: [{}] }], ["weighted-lora.safetensors"]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-lora")
            ?.click();

        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loraWeights,
        ).toEqual([0.65]);
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
        setup([{ duration: 4, stages: [{}] }], ["weighted-lora.safetensors"]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-lora")
            ?.click();

        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loraWeights,
        ).toEqual([0.55]);
    });

    it("resets per-stage IC-LoRA strengths from the selected model default", () => {
        modelGlobals.sdLoraBrowser = {
            models: {
                "weighted-ic.safetensors": {
                    data: { lora_default_weight: "0.4" },
                },
            },
        };
        setup(
            [
                {
                    duration: 4,
                    stages: [{}],
                    icLoras: [
                        {
                            lora: "lora-x.safetensors",
                            driveData: "visual",
                        },
                    ],
                },
            ],
            ["lora-x.safetensors", "weighted-ic.safetensors"],
        );
        setSelection({ kind: "ic-lora", clipIdx: 0, entryIdx: 0 });
        const select =
            fieldByLabel("LoRA").querySelector<HTMLSelectElement>("select");
        if (!select) throw new Error("IC-LoRA model select missing");
        select.value = "weighted-ic.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].icLoraStrengths,
        ).toEqual([0.4]);
    });

    it("uses zero-based LoRA labels and opens the newly added LoRA", () => {
        setup(
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
        expect(groups[0].querySelector(".header-label")?.textContent).toBe(
            "L0",
        );
        expect(groups[1].querySelector(".header-label")?.textContent).toBe(
            "L1",
        );
        expect(groups[0].classList.contains("input-group-closed")).toBe(true);
        expect(groups[1].classList.contains("input-group-open")).toBe(true);
    });

    it("renders clip LoRA model rows and flat numeric stage weights", () => {
        setup([
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
        expect(row?.querySelector("input.auto-number")).toBeNull();
        const weight = document.querySelector<HTMLInputElement>(
            '.vst-detail input[data-vst-focus-key="lora-weight-0"]',
        );
        expect(weight?.value).toBe("0.7");
        expect(weight?.step).toBe("0.05");
        expect(weight?.hasAttribute("min")).toBe(false);
        expect(weight?.hasAttribute("max")).toBe(false);
        expect(row?.querySelector("input.auto-slider-range")).toBeNull();
        expect(weight?.classList.contains("lora-weight-input")).toBe(true);
        const weightRow = weight?.closest<HTMLElement>(
            ".vst-stage-lora-weight-row",
        );
        const weightLabel = weightRow?.querySelector<HTMLLabelElement>("label");
        expect(weightRow).not.toBeNull();
        expect(weightLabel?.textContent).toBe("lora-x.safetensors");
        expect(weight?.id).not.toBe("");
        expect(weightLabel?.htmlFor).toBe(weight?.id);
        expect(
            detailBody()?.querySelector(".vst-detail-delete-lora"),
        ).not.toBeNull();
    });

    it("debounces a LoRA weight edit through the keyed pending map", () => {
        setup([
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
            '.vst-detail input[data-vst-focus-key="lora-weight-0"]',
        );
        if (!weight) {
            throw new Error("lora weight input missing");
        }
        expect(weight.getAttribute("data-vst-focus-key")).toBe("lora-weight-0");
        weight.value = "0.4";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loraWeights[0],
        ).toBe(0.4);
    });

    it("allows a negative LoRA weight through the number input", () => {
        setup([
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
            '.vst-detail input[data-vst-focus-key="lora-weight-0"]',
        );
        if (!weight) throw new Error("lora weight input missing");
        weight.value = "-2.5";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loraWeights[0],
        ).toBe(-2.5);
    });

    it("removes a LoRA row (flush-first) through saveClips", () => {
        setup([
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
            { name: "lora-y.safetensors" },
        ]);
        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loraWeights,
        ).toEqual([0.5]);
        expect(
            document.querySelectorAll(".vst-detail .vst-clip-lora-entry"),
        ).toHaveLength(1);
        expect(
            document
                .querySelector(".vst-detail .vst-detail-loras-section")
                ?.classList.contains("input-group-open"),
        ).toBe(true);
    });

    it("copies every LoRA and weight into a newly added stage", () => {
        setup([
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
        expect(stages[1].loraWeights).toEqual([0.65]);
    });
});
