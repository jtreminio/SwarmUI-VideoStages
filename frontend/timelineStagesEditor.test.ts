import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import * as persistence from "./persistence";
import {
    createTimelineStagesEditor,
    type TimelineStagesEditor,
} from "./timelineStagesEditor";
import { renderTimeline } from "./timelineView";
import type { Clip } from "./types";

interface StageFixture {
    model?: string;
    skipped?: boolean;
    loras?: { name: string; weight: number }[];
}

interface ClipFixture {
    duration: number;
    stages: StageFixture[];
    refs?: { source: string; frame: number }[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    audioSource: "Native",
    stages: clip.stages.map((s) => ({
        model: s.model ?? "model-a.safetensors",
        skipped: s.skipped ?? false,
        loras: s.loras ?? [],
    })),
    refs: clip.refs ?? [],
    promptWindows: [],
});

const mountRootDefaults = (loras: string[] = ["lora-x.safetensors"]): void => {
    mountSelect("input_videomodel", {
        value: "model-a.safetensors",
        options: ["model-a.safetensors", "model-b.safetensors"],
    });
    mountSelect("input_loras", { options: loras });
    mountSelect("input_sampler", { options: ["euler", "dpm"] });
    mountSelect("input_scheduler", { options: ["normal", "karras"] });
    mountSelect("input_refinerupscalemethod", {
        options: ["latentmodel-a.safetensors", "pixel-lanczos"],
    });
};

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    document.body.appendChild(body);
    return body;
};

const stageInspector = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-stage-inspector");
const modelInspector = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-stage-model-inspector");

const fieldLabels = (root: HTMLElement): string[] =>
    Array.from(
        root.querySelectorAll<HTMLElement>(
            ".vst-audio-field-label, .vst-stage-slider .auto-input-name",
        ),
    ).map((el) => el.textContent ?? "");

/** Number input of a native slider widget, located by its label text. */
const sliderNumberByLabel = (
    root: HTMLElement,
    label: string,
): HTMLInputElement => {
    const box = Array.from(
        root.querySelectorAll<HTMLElement>(".vst-stage-slider"),
    ).find((el) => el.querySelector(".auto-input-name")?.textContent === label);
    const input = box?.querySelector<HTMLInputElement>(
        "input.auto-slider-number",
    );
    if (!input) {
        throw new Error(`stage slider not found: ${label}`);
    }
    return input;
};

const fieldByLabel = (root: HTMLElement, label: string): HTMLElement => {
    const rows = Array.from(
        root.querySelectorAll<HTMLElement>(".vst-audio-field"),
    );
    const row = rows.find(
        (r) => r.querySelector(".vst-audio-field-label")?.textContent === label,
    );
    if (!row) {
        throw new Error(`stage field not found: ${label}`);
    }
    return row;
};

const clickAway = (): void => {
    document.body.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
};

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[spy.mock.calls.length - 1][0] as Clip[];

describe("createTimelineStagesEditor", () => {
    let track: TimelineStagesEditor | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        saveSpy = jest
            .spyOn(persistence, "saveClips")
            .mockImplementation(() => {});
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    const setup = (
        fixtures: ClipFixture[],
        loras: string[] = ["lora-x.safetensors"],
    ): HTMLElement => {
        mountPromptBox("");
        mountRootDefaults(loras);
        mountVideoStagesData({ clips: fixtures.map(clipRecord) });
        const body = makeBody();
        renderTimeline(body, persistence.getClips());
        track = createTimelineStagesEditor();
        track.attach(body);
        return body;
    };

    const clickStageChip = (
        body: HTMLElement,
        clipIdx: number,
        stageIdx: number,
        shift = false,
    ): void => {
        const chip = body.querySelector<HTMLElement>(
            `[data-vst-stage][data-clip-idx="${clipIdx}"][data-stage-idx="${stageIdx}"]`,
        );
        if (!chip) {
            throw new Error(`stage chip not found: ${clipIdx}/${stageIdx}`);
        }
        chip.dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: shift }),
        );
    };

    it("opens the stage editor when a stage chip is clicked", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        expect(stageInspector()).toBeNull();
        clickStageChip(body, 0, 0);
        const insp = stageInspector();
        expect(insp).not.toBeNull();
        expect(
            insp?.querySelector(".vst-prompt-inspector-head")?.textContent,
        ).toBe("Stage 0 · full gen");
    });

    it("shows Control/Upscale only for refine stages (index >= 1)", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);

        clickStageChip(body, 0, 0);
        const stage0 = stageInspector();
        if (!stage0) {
            throw new Error("stage 0 inspector missing");
        }
        const labels0 = fieldLabels(stage0);
        expect(labels0).not.toContain("Control (regen strength)");
        expect(labels0).not.toContain("Upscale");
        expect(
            stage0.querySelector(".vst-prompt-inspector-head")?.textContent,
        ).toBe("Stage 0 · full gen");
        // Close before opening the next one.
        clickAway();

        clickStageChip(body, 0, 1);
        const stage1 = stageInspector();
        if (!stage1) {
            throw new Error("stage 1 inspector missing");
        }
        const labels1 = fieldLabels(stage1);
        expect(labels1).toContain("Control (regen strength)");
        expect(labels1).toContain("Upscale");
        expect(labels1).toContain("Upscale Method");
        expect(
            stage1.querySelector(".vst-prompt-inspector-head")?.textContent,
        ).toBe("Stage 1 · refine");
    });

    it("shift+click deletes a stage only when more than one remains", () => {
        // Single stage: guarded, no delete.
        const oneBody = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(oneBody, 0, 0, true);
        expect(saveSpy).not.toHaveBeenCalled();
        track?.dispose();
        document.body.innerHTML = "";
        saveSpy.mockClear();

        // Two stages: shift+click removes the targeted one.
        const twoBody = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(twoBody, 0, 1, true);
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
    });

    it("the Delete stage button is present only when more than one stage exists", () => {
        const oneBody = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(oneBody, 0, 0);
        expect(stageInspector()?.querySelector(".vst-refs-delete")).toBeNull();
        track?.dispose();
        document.body.innerHTML = "";

        const twoBody = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(twoBody, 0, 1);
        expect(
            stageInspector()?.querySelector(".vst-refs-delete"),
        ).not.toBeNull();
    });

    it("adds a refine stage from the + chip and opens its editor", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        const add = body.querySelector<HTMLElement>("[data-vst-stage-add]");
        add?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(2);
    });

    it("opens the model popover and updates stage 0's model on change", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        const badge = body.querySelector<HTMLElement>("[data-vst-model]");
        badge?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        const insp = modelInspector();
        expect(insp).not.toBeNull();
        const select = insp?.querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        select.value = "model-b.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].model).toBe(
            "model-b.safetensors",
        );
        expect(modelInspector()).toBeNull();
    });

    it("commits a model change made in the stage editor on click-away", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = stageInspector();
        if (!insp) {
            throw new Error("stage inspector missing");
        }
        const select = fieldByLabel(
            insp,
            "Model",
        ).querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        select.value = "model-b.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].model).toBe(
            "model-b.safetensors",
        );
    });

    it("adds and persists a LoRA row", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = stageInspector();
        if (!insp) {
            throw new Error("stage inspector missing");
        }
        const addLora = insp.querySelector<HTMLElement>(".vst-stage-lora-add");
        addLora?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(insp.querySelectorAll(".vst-stage-lora-row")).toHaveLength(1);
        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 1 },
        ]);
    });

    it("shows a muted hint when no LoRAs are available", () => {
        const body = setup([{ duration: 4, stages: [{}] }], []);
        clickStageChip(body, 0, 0);
        const insp = stageInspector();
        expect(insp?.querySelector(".vst-stage-lora-add")).toBeNull();
        expect(insp?.textContent).toContain("(no LoRAs available)");
    });

    it("renders numeric fields as native Swarm slider widgets", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = stageInspector();
        if (!insp) {
            throw new Error("stage inspector missing");
        }
        // Steps/CFG became slider boxes; selects are still plain rows.
        expect(
            insp.querySelectorAll(".vst-stage-slider .auto-slider-box"),
        ).not.toHaveLength(0);
        expect(sliderNumberByLabel(insp, "Steps")).toBeInstanceOf(
            HTMLInputElement,
        );
        expect(
            fieldByLabel(insp, "Model").querySelector("select"),
        ).not.toBeNull();
    });

    it("commits a slider number edit to the saved stage on click-away", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = stageInspector();
        if (!insp) {
            throw new Error("stage inspector missing");
        }
        const steps = sliderNumberByLabel(insp, "Steps");
        steps.value = "12";
        steps.dispatchEvent(new Event("input", { bubbles: true }));
        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].steps).toBe(12);
    });

    it("keeps the editor open for keys inside a .sui-popover but Escape elsewhere cancels", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = stageInspector();
        if (!insp) {
            throw new Error("stage inspector missing");
        }
        // A key event originating inside the native dropdown popover must not
        // dismiss the whole inspector.
        const popover = document.createElement("div");
        popover.className = "sui-popover";
        const search = document.createElement("input");
        popover.appendChild(search);
        insp.appendChild(popover);
        search.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        expect(stageInspector()).not.toBeNull();
        expect(saveSpy).not.toHaveBeenCalled();

        // Escape elsewhere in the wrap still cancels (no save).
        insp.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        expect(stageInspector()).toBeNull();
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("clamps a bottom-docked popover to stay within the viewport", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        // Simulate a bottom-docked timeline: the body sits low in a short
        // viewport, and the popover is taller than the space below its anchor.
        Object.defineProperty(window, "innerHeight", {
            configurable: true,
            value: 600,
        });
        body.getBoundingClientRect = () =>
            ({
                top: 500,
                left: 0,
                width: 400,
                height: 100,
                right: 400,
                bottom: 600,
            }) as DOMRect;
        const heightDesc = Object.getOwnPropertyDescriptor(
            HTMLElement.prototype,
            "offsetHeight",
        );
        Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
            configurable: true,
            get() {
                return 400;
            },
        });
        try {
            clickStageChip(body, 0, 0);
            const insp = stageInspector();
            if (!insp) {
                throw new Error("stage inspector missing");
            }
            const top = Number.parseFloat(insp.style.top);
            // Without clamping, top would be 546 (500 + 46) and bottom 946.
            expect(top).toBeLessThanOrEqual(600 - 400 - 8);
            expect(top + 400).toBeLessThanOrEqual(600 - 8);
            expect(insp.style.maxHeight).not.toBe("");
        } finally {
            if (heightDesc) {
                Object.defineProperty(
                    HTMLElement.prototype,
                    "offsetHeight",
                    heightDesc,
                );
            } else {
                delete (
                    HTMLElement.prototype as unknown as Record<string, unknown>
                ).offsetHeight;
            }
        }
    });

    it("does not select or delete the clip when a stage chip is shift+clicked", () => {
        // The editor must swallow the event before the region handler runs.
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(body, 0, 1, true);
        // Only the stage was removed; the clip itself remains.
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
        expect(saveSpy.mock.calls).toHaveLength(1);
    });
});
