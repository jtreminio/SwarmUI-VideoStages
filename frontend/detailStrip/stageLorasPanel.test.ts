import { describe, expect, it, jest } from "@jest/globals";
import { lastSavedClips, requireEl } from "../__test_helpers__/dom";
import { editorHarness, modelGlobals } from "../__test_helpers__/editor";
import { getState } from "../persistence/repository";
import { setSelection } from "../selection";
import type { Clip } from "../types";

describe("production editor stage LoRA panel", () => {
    const h = editorHarness();
    it("copies pending strengths to every other stage of only the current clip", () => {
        const source = [{ name: "lora-y.safetensors", weight: 0.65 }];
        h.setup(
            [
                {
                    duration: 4,
                    stages: [
                        {
                            loras: [
                                { name: "lora-x.safetensors", weight: 0.2 },
                            ],
                        },
                    ],
                },
                {
                    duration: 4,
                    stages: [
                        {
                            loras: [
                                { name: "lora-x.safetensors", weight: 0.4 },
                            ],
                        },
                        { loras: source },
                        { skipped: true, loras: [] },
                    ],
                },
            ],
            ["lora-x.safetensors", "lora-y.safetensors"],
        );
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 1 });
        const strength = requireEl(
            h.item("stages", 1),
            ".vst-stage-lora-strength",
        ) as HTMLInputElement;
        strength.focus();
        strength.value = "-0.75";
        strength.dispatchEvent(new Event("input", { bubbles: true }));
        h.item("stages", 1)
            .querySelector<HTMLButtonElement>(".vst-stage-loras-copy")
            ?.click();
        const clips = getState().clips;
        expect(clips[0].stages[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 0.2 },
        ]);
        expect(clips[1].stages.map((stage) => stage.loras)).toEqual(
            Array.from({ length: 3 }, () => [
                { name: "lora-y.safetensors", weight: -0.75 },
            ]),
        );
        h.item("stages", 0)
            .querySelector<HTMLButtonElement>(".vst-stage-lora-remove")
            ?.click();
        expect(
            getState().clips[1].stages.map((stage) => stage.loras.length),
        ).toEqual([0, 1, 1]);
    });

    it("allows the same model on different stages and resets only the changed row's strength", () => {
        modelGlobals.sdLoraBrowser = {
            models: {
                "lora-y.safetensors": { data: { lora_default_weight: "0.3" } },
            },
        };
        h.setup(
            [
                {
                    duration: 4,
                    stages: [
                        {
                            loras: [
                                { name: "lora-x.safetensors", weight: 0.7 },
                            ],
                        },
                        {
                            loras: [
                                { name: "lora-y.safetensors", weight: -0.2 },
                            ],
                        },
                    ],
                },
            ],
            ["lora-x.safetensors", "lora-y.safetensors"],
        );
        const model = field<HTMLSelectElement>(0, "select");
        expect([...model.options].map((option) => option.value)).toContain(
            "lora-y.safetensors",
        );
        model.value = "lora-y.safetensors";
        model.dispatchEvent(new Event("change", { bubbles: true }));
        expect(getState().clips[0].stages.map((stage) => stage.loras)).toEqual([
            [{ name: "lora-y.safetensors", weight: 0.3 }],
            [{ name: "lora-y.safetensors", weight: -0.2 }],
        ]);
        h.item("stages", 1)
            .querySelector<HTMLButtonElement>(".vst-stage-lora-add")
            ?.click();
        expect(
            getState().clips[0].stages.map((stage) => stage.loras.length),
        ).toEqual([1, 2]);
    });

    it("offers copying only from a populated stage with another destination", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 1 }] },
                ],
            },
        ]);
        expect(
            h.item("stages", 0).querySelector(".vst-stage-loras-copy"),
        ).toBeNull();
        h.section("stages")
            .querySelector<HTMLButtonElement>("[data-testid=add-stages]")
            ?.click();
        expect(
            h.item("stages", 0).querySelector(".vst-stage-loras-copy")
                ?.textContent,
        ).toBe("Copy to other stages");
        expect(
            h.item("stages", 1).querySelector(".vst-stage-loras-copy")
                ?.textContent,
        ).toBe("Copy to other stages");
        requireEl(h.item("stages", 1), ".vst-stage-lora-remove").click();
        expect(
            h.item("stages", 1).querySelector(".vst-stage-loras-copy"),
        ).toBeNull();
        expect(h.item("stages", 1).textContent).toContain(
            "No LoRAs on this stage.",
        );
    });
    const row = (index: number, stage = 0): HTMLElement => {
        const found = h
            .item("stages", stage)
            .querySelectorAll<HTMLElement>(".vst-stage-lora-row")[index];
        if (!found) throw new Error("Missing LoRA row");
        return found;
    };
    const field = <T extends HTMLElement>(
        index: number,
        selector: string,
    ): T => {
        const found = row(index).querySelector<T>(selector);
        if (!found) throw new Error("Missing LoRA field");
        return found;
    };

    it("adds and persists a LoRA row", () => {
        h.setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLElement>(".vst-clip-editor .vst-stage-lora-add")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(document.querySelectorAll(".vst-stage-lora-row")).toHaveLength(
            1,
        );
        expect(h.saveSpy).toHaveBeenCalled();
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 1 },
        ]);
        expect(
            h.item("stages", 0).querySelector(".vst-stage-lora-add")
                ?.textContent,
        ).toBe("+ Add LoRA");
    });

    it("seeds a new stage LoRA from SwarmUI model metadata", () => {
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
            .querySelector<HTMLButtonElement>(".vst-stage-lora-add")
            ?.click();

        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loras[0],
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
            .querySelector<HTMLButtonElement>(".vst-stage-lora-add")
            ?.click();

        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loras[0],
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
        h.item("stages", 0)
            .querySelector<HTMLButtonElement>(".vst-stage-lora-add")
            ?.click();
        expect(
            h.item("stages", 0).querySelectorAll(".vst-stage-lora-row"),
        ).toHaveLength(2);
        expect(field<HTMLSelectElement>(0, "select").value).toBe(
            "lora-x.safetensors",
        );
        expect(field<HTMLSelectElement>(1, "select").value).toBe(
            "lora-y.safetensors",
        );
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
        const firstRow = row(0);
        const remove = requireEl(firstRow, ".vst-stage-lora-remove");
        expect(remove.querySelector("svg")).not.toBeNull();
        expect(remove.getAttribute("aria-label")).toBe(
            "Remove LoRA 1 from stage S0",
        );
        expect(field<HTMLSelectElement>(0, "select").value).toBe(
            "lora-x.safetensors",
        );
        const weight = field<HTMLInputElement>(0, "input");
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
            '.vst-clip-editor input[data-vst-focus-key$="lora-0-strength"]',
        );
        if (!weight) {
            throw new Error("lora weight input missing");
        }
        expect(weight.getAttribute("data-vst-focus-key")).toEqual(
            expect.stringMatching(/\/lora-0-strength$/),
        );
        weight.value = "0.4";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        expect(h.saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        expect(h.saveSpy).toHaveBeenCalledTimes(1);
        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loras[0],
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
            '.vst-clip-editor input[data-vst-focus-key$="lora-0-strength"]',
        );
        if (!weight) throw new Error("lora weight input missing");
        weight.value = "-2.5";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(
            Reflect.get(
                lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loras[0],
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
        expect(document.querySelectorAll(".vst-stage-lora-row")).toHaveLength(
            2,
        );
        document
            .querySelectorAll<HTMLElement>(".vst-stage-lora-remove")[0]
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(lastSavedClips<Clip[]>(h.saveSpy)[0].stages[0].loras).toEqual([
            { name: "lora-y.safetensors", weight: 0.5 },
        ]);
        expect(document.querySelectorAll(".vst-stage-lora-row")).toHaveLength(
            1,
        );
        expect(
            h.item("stages", 0).querySelector(".vst-stage-lora-add")
                ?.textContent,
        ).toBe("+ Add LoRA");
    });

    it("copies the last stage's LoRAs and pending strengths when adding a stage", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 0.2 }] },
                    {
                        loras: [
                            { name: "lora-y.safetensors", weight: 0.65 },
                            { name: "lora-x.safetensors", weight: 0 },
                        ],
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const strength = requireEl(
            h.item("stages", 1),
            ".vst-stage-lora-strength",
        ) as HTMLInputElement;
        strength.focus();
        strength.value = "-0.75";
        strength.dispatchEvent(new Event("input", { bubbles: true }));
        requireEl(document, "[data-testid='add-stages']").click();
        const expected = [
            { name: "lora-y.safetensors", weight: -0.75 },
            { name: "lora-x.safetensors", weight: 0 },
        ];
        expect(getState().clips[0].stages.map((stage) => stage.loras)).toEqual([
            [{ name: "lora-x.safetensors", weight: 0.2 }],
            expected,
            expected,
        ]);
        requireEl(h.item("stages", 2), ".vst-stage-lora-remove").click();
        expect(
            lastSavedClips<Clip[]>(h.saveSpy)[0].stages.map(
                (stage) => stage.loras,
            ),
        ).toEqual([
            [{ name: "lora-x.safetensors", weight: 0.2 }],
            expected,
            [{ name: "lora-x.safetensors", weight: 0 }],
        ]);
    });

    it("starts empty when the last stage has no LoRAs", () => {
        h.setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 0.65 }] },
                    { loras: [] },
                ],
            },
        ]);
        requireEl(document, "[data-testid='add-stages']").click();
        expect(getState().clips[0].stages.map((stage) => stage.loras)).toEqual([
            [{ name: "lora-x.safetensors", weight: 0.65 }],
            [],
            [],
        ]);
    });
});
