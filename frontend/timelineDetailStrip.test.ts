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
    createTimelineDetailStrip,
    type TimelineDetailStrip,
} from "./timelineDetailStrip";
import { renderTimeline } from "./timelineView";
import type { Clip } from "./types";
import { getSelection, resetSelectionForTests, setSelection } from "./uiState";

interface StageFixture {
    model?: string;
    skipped?: boolean;
    loras?: { name: string; weight: number }[];
    upscale?: number;
    control?: number;
    steps?: number;
}

interface WindowFixture {
    prompt?: string;
    start: number;
    duration: number;
}

interface ClipFixture {
    duration: number;
    stages: StageFixture[];
    refs?: { source: string; frame: number }[];
    audioSource?: string;
    controlNetLora?: string;
    reuseAudio?: boolean;
    clipLengthFromAudio?: boolean;
    prompt?: string;
    windows?: WindowFixture[];
    boundaryOut?: "cut" | "continue" | "crossfade";
    retake?: {
        startSeconds: number;
        lengthSeconds: number;
        strength: number;
    };
    audioSegments?: {
        source: { data: string; fileName: string };
        startSeconds: number;
        trimStartSeconds: number;
        lengthSeconds: number;
    }[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    boundaryOut: clip.boundaryOut ?? "cut",
    audioSource: clip.audioSource ?? "Native",
    controlNetLora: clip.controlNetLora ?? "",
    reuseAudio: clip.reuseAudio ?? false,
    clipLengthFromAudio: clip.clipLengthFromAudio ?? false,
    stages: clip.stages.map((s) => ({
        model: s.model ?? "model-a.safetensors",
        skipped: s.skipped ?? false,
        loras: s.loras ?? [],
        upscale: s.upscale,
        control: s.control,
        steps: s.steps,
    })),
    refs: clip.refs ?? [],
    promptWindows: [],
    ...(clip.retake ? { retake: clip.retake } : {}),
    ...(clip.audioSegments ? { audioSegments: clip.audioSegments } : {}),
});

// Prompt windows + clip prompts ride in the prompt box as tags.
const promptText = (fixtures: ClipFixture[]): string => {
    const tags: string[] = [];
    fixtures.forEach((clip, i) => {
        if (clip.prompt) {
            tags.push(`<videoclip[${i}]>${clip.prompt}`);
        }
        for (const w of clip.windows ?? []) {
            const end = w.start + w.duration;
            tags.push(`<videoclip[${i}]:${w.start}-${end}>${w.prompt ?? ""}`);
        }
    });
    return tags.join("\n");
};

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

const detail = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-detail");

const detailBody = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-detail-body");

const crumbText = (): string | undefined =>
    detail()?.querySelector<HTMLElement>(".vst-detail-crumb")?.textContent ??
    undefined;

const railChips = (): HTMLElement[] =>
    Array.from(
        document.querySelectorAll<HTMLElement>(
            ".vst-detail-rail-list .vst-stage-tab:not(.vst-stage-tab-add)",
        ),
    );

const activeRailLabel = (): string | undefined =>
    document.querySelector<HTMLElement>(
        ".vst-detail-rail-list .vst-stage-tab-active",
    )?.textContent ?? undefined;

const sliderNumberByLabel = (label: string): HTMLInputElement => {
    const box = Array.from(
        document.querySelectorAll<HTMLElement>(".vst-detail .vst-stage-slider"),
    ).find((el) => el.querySelector(".auto-input-name")?.textContent === label);
    const input = box?.querySelector<HTMLInputElement>(
        "input.auto-slider-number",
    );
    if (!input) {
        throw new Error(`slider not found: ${label}`);
    }
    return input;
};

const fieldByLabel = (label: string): HTMLElement => {
    const rows = Array.from(
        document.querySelectorAll<HTMLElement>(".vst-detail .vst-audio-field"),
    );
    const row = rows.find(
        (r) => r.querySelector(".vst-audio-field-label")?.textContent === label,
    );
    if (!row) {
        throw new Error(`field not found: ${label}`);
    }
    return row;
};

const checkboxByLabel = (label: string): HTMLInputElement => {
    const rows = Array.from(
        document.querySelectorAll<HTMLElement>(
            ".vst-detail .vst-audio-field-check",
        ),
    );
    const row = rows.find(
        (r) => r.querySelector(".vst-audio-field-label")?.textContent === label,
    );
    const input = row?.querySelector<HTMLInputElement>("input[type=checkbox]");
    if (!input) {
        throw new Error(`checkbox not found: ${label}`);
    }
    return input;
};

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[spy.mock.calls.length - 1][0] as Clip[];

describe("createTimelineDetailStrip", () => {
    let strip: TimelineDetailStrip | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;
    let collapsed = false;

    beforeEach(() => {
        resetSelectionForTests();
        persistence.__resetPersistenceForTests();
        collapsed = false;
        // Spy but call through, so persistence actually updates and the
        // strip's `getClips()` reflects each live-apply write.
        saveSpy = jest.spyOn(persistence, "saveClips");
    });

    afterEach(() => {
        strip?.dispose();
        strip = null;
        jest.useRealTimers();
        jest.restoreAllMocks();
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    let refreshSpy: jest.Mock;

    const setup = (
        fixtures: ClipFixture[],
        loras: string[] = ["lora-x.safetensors"],
    ): HTMLElement => {
        mountPromptBox(promptText(fixtures));
        mountRootDefaults(loras);
        mountVideoStagesData({ clips: fixtures.map(clipRecord) });
        const body = makeBody();
        renderTimeline(body, persistence.getClips());
        refreshSpy = jest.fn();
        strip = createTimelineDetailStrip({
            isCollapsed: () => collapsed,
            setCollapsed: (value) => {
                collapsed = value;
            },
            refresh: () => refreshSpy(),
        });
        strip.attach(body);
        return body;
    };

    const clickRegionStageChip = (
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

    it("renders the timeline settings panel when nothing is selected", () => {
        setup([{ duration: 4, stages: [{}] }]);
        expect(detail()).not.toBeNull();
        expect(crumbText()).toBe("Timeline settings");
        expect(detail()?.querySelector(".vst-detail-settings")).not.toBeNull();
        // Resolution / Dimensions / FPS controls are present.
        const labels = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .vst-audio-field-label",
            ),
        ).map((el) => el.textContent);
        expect(labels).toEqual(
            expect.arrayContaining(["Resolution", "Dimensions", "FPS"]),
        );
        // Width and Height inputs live side-by-side in the Dimensions pair.
        const dims = detail()?.querySelector<HTMLElement>(".vst-settings-dims");
        expect(dims).not.toBeNull();
        expect(dims?.querySelectorAll("input")).toHaveLength(2);
        expect(
            dims
                ?.querySelector<HTMLInputElement>(
                    'input[data-vst-focus-key="settings-width"]',
                )
                ?.getAttribute("data-vst-focus-key"),
        ).toBe("settings-width");
        expect(
            dims?.querySelector('input[data-vst-focus-key="settings-height"]'),
        ).not.toBeNull();
    });

    it("renders the clip/stage columns when a stage chip is clicked", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickRegionStageChip(body, 0, 1);
        expect(crumbText()).toBe("Clip 0 · S1");
        expect(activeRailLabel()).toBe("S1");
        expect(detailBody()?.querySelector(".vst-detail-clip")).not.toBeNull();
        expect(detailBody()?.querySelector(".vst-detail-rail")).not.toBeNull();
        expect(
            detailBody()?.querySelector(".vst-detail-params"),
        ).not.toBeNull();
        // LoRAs now live inside the params grid as a full-width section, not
        // a separate right-hand column.
        expect(detailBody()?.querySelector(".vst-detail-loras")).toBeNull();
        const loras = detailBody()?.querySelector<HTMLElement>(
            ".vst-detail-params .vst-stage-loras",
        );
        expect(loras).not.toBeNull();
        expect(loras?.classList.contains("vst-detail-span-full")).toBe(true);
    });

    it("switches the active stage when a rail chip is clicked", () => {
        setup([{ duration: 4, stages: [{ steps: 5 }, { steps: 9 }] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(activeRailLabel()).toBe("S0");
        expect(sliderNumberByLabel("Steps").value).toBe("5");

        railChips()[1].dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(activeRailLabel()).toBe("S1");
        expect(sliderNumberByLabel("Steps").value).toBe("9");
    });

    it("shows Control/Upscale only on refine stages", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(
            document.querySelector(".vst-detail .auto-input-name"),
        ).not.toBeNull();
        const labels0 = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .auto-input-name",
            ),
        ).map((el) => el.textContent);
        expect(labels0).not.toContain("Control");
        expect(labels0).not.toContain("Upscale");

        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const labels1 = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .auto-input-name",
            ),
        ).map((el) => el.textContent);
        expect(labels1).toContain("Control");
        expect(labels1).toContain("Upscale");
        expect(
            fieldByLabel("Upscale Method").querySelector("select"),
        ).not.toBeNull();
    });

    it("live-applies a discrete select change through saveClips", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Model").querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        select.value = "model-b.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalled();
        expect(savedClips(saveSpy)[0].stages[0].model).toBe(
            "model-b.safetensors",
        );
    });

    it("debounces a continuous slider change and flushes it through saveClips", () => {
        setup([{ duration: 4, stages: [{ steps: 8 }] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const steps = sliderNumberByLabel("Steps");
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));
        // Not written until the debounce window elapses.
        expect(saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].steps).toBe(14);
    });

    it("drops a pending change when the carrier went stale", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Model").querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        // Something else mutates the carrier: the token is now stale.
        mountPromptBox("changed by someone else");
        select.value = "model-b.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("clears the selection to none on Escape", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(crumbText()).toBe("Clip 0 · S0");
        detail()?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        // "none" now renders the timeline settings panel.
        expect(crumbText()).toBe("Timeline settings");
        expect(getSelection().kind).toBe("none");
    });

    it("keeps the selection when Escape fires inside a .sui-popover", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const popover = document.createElement("div");
        popover.className = "sui-popover";
        const search = document.createElement("input");
        popover.appendChild(search);
        detail()?.appendChild(popover);
        search.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        expect(crumbText()).toBe("Clip 0 · S0");
    });

    it("shift+clicking a region stage chip deletes the stage", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickRegionStageChip(body, 0, 1, true);
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
    });

    it("adds a stage from the rail + button and selects it", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLElement>(
                ".vst-detail-rail-list .vst-stage-tab-add",
            )
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(2);
        expect(activeRailLabel()).toBe("S1");
        expect(crumbText()).toBe("Clip 0 · S1");
    });

    it("mutes the stage params and persists Skip this stage", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const fields =
            document.querySelector<HTMLElement>(".vst-detail-fields");
        expect(fields?.classList.contains("vst-stage-fields-muted")).toBe(
            false,
        );
        const skip = checkboxByLabel("Skip this stage");
        skip.checked = true;
        skip.dispatchEvent(new Event("change", { bubbles: true }));
        expect(fields?.classList.contains("vst-stage-fields-muted")).toBe(true);
        expect(saveSpy).toHaveBeenCalled();
        expect(savedClips(saveSpy)[0].stages[1].skipped).toBe(true);
    });

    it("commits a clip Duration edit through applyClipDurationResize", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const dur =
            fieldByLabel("Duration (s)").querySelector<HTMLInputElement>(
                "input",
            );
        if (!dur) {
            throw new Error("duration input missing");
        }
        dur.value = "6";
        dur.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].duration).toBe(6);
    });

    it("disables the Duration field when clip length is derived from audio", () => {
        setup([
            {
                duration: 4,
                audioSource: "Upload",
                clipLengthFromAudio: true,
                stages: [{}],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const field = fieldByLabel("Duration (s)");
        expect(field.querySelector<HTMLInputElement>("input")?.disabled).toBe(
            true,
        );
        expect(field.classList.contains("vst-field-disabled")).toBe(true);
    });

    it("shows a + Retake button on a clip without a retake and creates+selects one", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLElement>(
            ".vst-detail-add-retake",
        );
        expect(addBtn).not.toBeNull();
        addBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        const retake = savedClips(saveSpy)[0].retake;
        expect(retake).not.toBeNull();
        expect(retake?.startSeconds).toBe(0);
        expect(retake?.lengthSeconds).toBe(2); // min(default 2, clip 4)
        expect(retake?.strength).toBe(1);
    });

    it("hides the + Retake button once a retake exists", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(document.querySelector(".vst-detail-add-retake")).toBeNull();
    });

    it("renders the retake editor with the breadcrumb, fields, note and remove", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 0.6 },
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(crumbText()).toBe("Retake · Clip 0 · 2–5 s");
        expect(
            fieldByLabel("Start (s)").querySelector<HTMLInputElement>("input")
                ?.value,
        ).toBe("2");
        expect(
            fieldByLabel("Length (s)").querySelector<HTMLInputElement>("input")
                ?.value,
        ).toBe("3");
        expect(sliderNumberByLabel("Strength").value).toBe("0.6");
        expect(
            detail()?.querySelector(".vst-detail-note")?.textContent,
        ).toContain("Applies when refining a base video");
        expect(
            detailBody()?.querySelector(".vst-detail-delete")?.textContent,
        ).toBe("Remove retake");
    });

    it("live-applies a retake Start edit through the debounce", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        jest.useFakeTimers();
        const start =
            fieldByLabel("Start (s)").querySelector<HTMLInputElement>("input");
        if (!start) {
            throw new Error("start input missing");
        }
        start.value = "4";
        start.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].retake?.startSeconds).toBe(4);
    });

    it("removes the retake and clears the selection", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        detailBody()
            ?.querySelector<HTMLElement>(".vst-detail-delete")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].retake).toBeNull();
        expect(getSelection()).toEqual({ kind: "none" });
    });

    it("falls back to no selection when a retake selection points at a clip with none", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "retake", clipIdx: 0 });
        // clampSelection drops it since the clip has no retake.
        expect(crumbText()).toBe("Timeline settings");
        expect(getSelection()).toEqual({ kind: "none" });
    });

    it("adds and persists a LoRA row", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLElement>(".vst-detail .vst-stage-lora-add")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(
            document.querySelectorAll(".vst-detail .vst-stage-lora-row"),
        ).toHaveLength(1);
        expect(saveSpy).toHaveBeenCalled();
        expect(savedClips(saveSpy)[0].stages[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 1 },
        ]);
    });

    it("renders a LoRA row with a name select and a weight input", () => {
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
            ".vst-detail .vst-stage-lora-row",
        );
        expect(row).not.toBeNull();
        const nameSelect = row?.querySelector<HTMLSelectElement>("select");
        expect(nameSelect?.value).toBe("lora-x.safetensors");
        // Name renders at input font size (via .vst-audio-select), not the
        // small 3xs label size.
        expect(nameSelect?.classList.contains("vst-audio-select")).toBe(true);
        const weight = row?.querySelector<HTMLInputElement>(
            ".vst-stage-lora-weight",
        );
        expect(weight?.value).toBe("0.7");
        expect(row?.querySelector(".vst-stage-lora-remove")).not.toBeNull();
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
            ".vst-detail .vst-stage-lora-weight",
        );
        if (!weight) {
            throw new Error("lora weight input missing");
        }
        expect(weight.getAttribute("data-vst-focus-key")).toBe("lora-0-weight");
        weight.value = "0.4";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        // Not written until the debounce window elapses.
        expect(saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].loras[0].weight).toBe(0.4);
    });

    it("removes a LoRA row (flush-first) through saveClips", () => {
        setup([
            {
                duration: 4,
                stages: [
                    {
                        loras: [
                            { name: "lora-x.safetensors", weight: 1 },
                            { name: "lora-x.safetensors", weight: 0.5 },
                        ],
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(
            document.querySelectorAll(".vst-detail .vst-stage-lora-row"),
        ).toHaveLength(2);
        document
            .querySelectorAll<HTMLElement>(
                ".vst-detail .vst-stage-lora-remove",
            )[0]
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].stages[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 0.5 },
        ]);
        expect(
            document.querySelectorAll(".vst-detail .vst-stage-lora-row"),
        ).toHaveLength(1);
    });

    it("deletes the current stage from the rail's Delete stage button", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const deleteBtn = document.querySelector<HTMLElement>(
            ".vst-detail-rail .vst-detail-delete-stage",
        );
        expect(deleteBtn).not.toBeNull();
        deleteBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
    });

    it("collapses and expands via the header chevron and persists the flag", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(detailBody()).not.toBeNull();

        detail()
            ?.querySelector<HTMLElement>(".vst-detail-collapse")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(collapsed).toBe(true);
        expect(detail()?.classList.contains("vst-detail-collapsed")).toBe(true);
        expect(detailBody()).toBeNull();

        // Selecting something new while collapsed auto-expands.
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        // (same selection is deduped; force a change to trigger expand)
        setSelection({ kind: "none" });
        collapsed = true;
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(collapsed).toBe(false);
        expect(detailBody()).not.toBeNull();
    });

    it("clamps the selection to none after its clip is removed", () => {
        const body = setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(crumbText()).toBe("Clip 1 · S0");

        // Remove clip 1 from the carrier, re-render the tracks + strip.
        const clips = persistence.getClips().slice(0, 1);
        persistence.saveClips(clips, undefined, { notifyDomChange: false });
        renderTimeline(body, persistence.getClips());
        strip?.render();
        expect(getSelection().kind).toBe("none");
        expect(crumbText()).toBe("Timeline settings");
    });

    // ---- Phase 2: ref / audio / prompt / settings editors ----------------

    it("renders the reference editor and live-applies + deletes", () => {
        setup([
            {
                duration: 5,
                stages: [{}],
                refs: [
                    { source: "Refiner", frame: 1 },
                    { source: "Base", frame: 2 },
                ],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 1 });
        expect(crumbText()).toBe("Ref 1 · Clip 0");

        const sourceSelect =
            fieldByLabel("Image Source").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!sourceSelect) {
            throw new Error("source select missing");
        }
        expect(Array.from(sourceSelect.options).map((o) => o.value)).toEqual([
            "Base",
            "Refiner",
            "Upload",
        ]);
        sourceSelect.value = "Upload";
        sourceSelect.dispatchEvent(new Event("change", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalled();
        expect(savedClips(saveSpy)[0].refs[1].source).toBe("Upload");

        // Delete removes the ref and clears the selection.
        document
            .querySelector<HTMLElement>(".vst-detail .vst-detail-delete")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].refs).toHaveLength(1);
        expect(getSelection().kind).toBe("none");
    });

    it("clamps an edited ref frame and writes it through saveClips", () => {
        setup([
            { duration: 5, stages: [{}], refs: [{ source: "Base", frame: 1 }] },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        jest.useFakeTimers();
        const frameRow = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .vst-audio-field",
            ),
        ).find((r) =>
            r
                .querySelector(".vst-audio-field-label")
                ?.textContent?.startsWith("Attach at Frame"),
        );
        const input = frameRow?.querySelector<HTMLInputElement>("input");
        if (!input) {
            throw new Error("frame input missing");
        }
        input.value = "7";
        input.dispatchEvent(new Event("change", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].refs[0].frame).toBe(7);
    });

    it("renders the audio editor and live-applies source + flags", () => {
        setup([{ duration: 5, stages: [{}], controlNetLora: "some-lora" }]);
        setSelection({ kind: "audio", clipIdx: 0 });
        expect(crumbText()).toBe("Audio · Clip 0");
        const select =
            fieldByLabel("Audio Source").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("audio source select missing");
        }
        expect(Array.from(select.options).map((o) => o.value)).toContain(
            "ControlNet",
        );
        // Clip Length from Audio is disabled for Native.
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(true);

        const reuse =
            fieldByLabel("Reuse Audio").querySelector<HTMLInputElement>(
                "input",
            );
        if (!reuse) {
            throw new Error("reuse checkbox missing");
        }
        reuse.checked = true;
        reuse.dispatchEvent(new Event("change", { bubbles: true }));
        expect(savedClips(saveSpy)[0].reuseAudio).toBe(true);

        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(savedClips(saveSpy)[0].audioSource).toBe("Upload");
        // Now upload-only length gating is available.
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(false);
    });

    it("shows + Add segment in the audio editor and creates+selects a segment", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "audio", clipIdx: 0 });
        const addBtn = document.querySelector<HTMLElement>(
            ".vst-detail-add-segment",
        );
        expect(addBtn).not.toBeNull();
        addBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 0,
        });
        const segments = savedClips(saveSpy)[0].audioSegments;
        expect(segments).toHaveLength(1);
        expect(segments[0].startSeconds).toBe(0);
        expect(segments[0].lengthSeconds).toBe(2); // min(default 2, clip 4)
        expect(segments[0].source).toBeNull();
    });

    it("renders the audio-segment editor with breadcrumb, fields and remove", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                audioSegments: [
                    {
                        source: {
                            data: "data:audio/wav;base64,QUJD",
                            fileName: "a.wav",
                        },
                        startSeconds: 2,
                        trimStartSeconds: 1,
                        lengthSeconds: 3,
                    },
                ],
            },
        ]);
        setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 0 });
        expect(crumbText()).toBe("Audio segment · Clip 0 · 2–5 s");
        expect(
            fieldByLabel("Start (s)").querySelector<HTMLInputElement>("input")
                ?.value,
        ).toBe("2");
        expect(
            fieldByLabel("Trim start (s)").querySelector<HTMLInputElement>(
                "input",
            )?.value,
        ).toBe("1");
        expect(
            fieldByLabel("Length (s)").querySelector<HTMLInputElement>("input")
                ?.value,
        ).toBe("3");
        expect(
            detailBody()?.querySelector(".vst-detail-delete")?.textContent,
        ).toBe("Remove segment");
    });

    it("live-applies an audio-segment Start edit and removes the segment", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                audioSegments: [
                    {
                        source: {
                            data: "data:audio/wav;base64,QUJD",
                            fileName: "a.wav",
                        },
                        startSeconds: 2,
                        trimStartSeconds: 0,
                        lengthSeconds: 3,
                    },
                ],
            },
        ]);
        setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 0 });
        jest.useFakeTimers();
        const start =
            fieldByLabel("Start (s)").querySelector<HTMLInputElement>("input");
        if (!start) {
            throw new Error("start input missing");
        }
        start.value = "4";
        start.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].audioSegments[0].startSeconds).toBe(4);
        jest.useRealTimers();

        const del =
            detailBody()?.querySelector<HTMLElement>(".vst-detail-delete");
        del?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(getSelection()).toEqual({ kind: "none" });
        expect(savedClips(saveSpy)[0].audioSegments).toEqual([]);
    });

    it("edits the clip's major prompt (debounced) through saveClips", () => {
        setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        expect(crumbText()).toBe("Prompt · Clip 0");
        jest.useFakeTimers();
        const editor =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!editor) {
            throw new Error("prompt textarea missing");
        }
        editor.value = "a wide landscape";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].prompt).toBe("a wide landscape");
    });

    it("edits and deletes a minor relay window", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                windows: [{ start: 2, duration: 3, prompt: "old" }],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        expect(crumbText()).toBe("Relay 2–5s · Clip 0");
        jest.useFakeTimers();
        const editor =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!editor) {
            throw new Error("minor textarea missing");
        }
        editor.value = "a red car";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].promptWindows[0].prompt).toBe(
            "a red car",
        );
        jest.useRealTimers();

        document
            .querySelector<HTMLElement>(".vst-detail .vst-detail-delete")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].promptWindows).toHaveLength(0);
        expect(getSelection().kind).toBe("none");
    });

    it("applies a resolution preset from the settings panel", () => {
        setup([{ duration: 4, stages: [{}] }]);
        // Default selection is "none" → the settings panel is shown.
        const select =
            fieldByLabel("Resolution").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("resolution select missing");
        }
        select.value = "512x768";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        const parsed = JSON.parse(
            document.querySelector<HTMLTextAreaElement>("#input_videostages")
                ?.value ?? "{}",
        );
        expect(parsed.width).toBe(512);
        expect(parsed.height).toBe(768);
        expect(refreshSpy).toHaveBeenCalled();
    });

    it("auto-expands the strip when a selection arrives while collapsed", () => {
        setup([{ duration: 4, stages: [{}] }]);
        collapsed = true;
        strip?.render();
        expect(detailBody()).toBeNull();
        setSelection({ kind: "audio", clipIdx: 0 });
        expect(collapsed).toBe(false);
        expect(detailBody()).not.toBeNull();
    });

    // ---- debounce coalescing / flush regression (no silent data loss) ----

    it("persists both fields when two debounced sliders change within one window", () => {
        setup([{ duration: 4, stages: [{ steps: 8 }] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const steps = sliderNumberByLabel("Steps");
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));
        const cfg = sliderNumberByLabel("CFG Scale");
        cfg.value = "9";
        cfg.dispatchEvent(new Event("input", { bubbles: true }));
        // Neither has been written yet.
        expect(saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        // A single coalesced write carries BOTH edits — no silent revert.
        const stage = savedClips(saveSpy)[0].stages[0];
        expect(stage.steps).toBe(14);
        expect(stage.cfgScale).toBe(9);
    });

    it("flushes a pending edit exactly once when the selection switches mid-window", () => {
        setup([
            { duration: 4, stages: [{ steps: 8 }] },
            { duration: 4, stages: [{ steps: 8 }] },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const steps = sliderNumberByLabel("Steps");
        steps.value = "14";
        steps.dispatchEvent(new Event("input", { bubbles: true }));

        // Switching clips before the debounce elapses must flush the edit.
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].steps).toBe(14);

        // The re-render's synthetic slider input must not schedule a spurious
        // write that fires later.
        jest.advanceTimersByTime(200);
        expect(saveSpy).toHaveBeenCalledTimes(1);
    });

    it("does not write when a selection change merely re-renders the strip", () => {
        setup([{ duration: 4, stages: [{ steps: 8 }] }]);
        jest.useFakeTimers();
        // Selecting a clip builds native sliders; enableSlidersIn fires
        // synthetic input events which must NOT schedule a commit.
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.advanceTimersByTime(200);
        expect(saveSpy).not.toHaveBeenCalled();
    });

    describe("boundary editor", () => {
        const boundarySelect = (): HTMLSelectElement => {
            const select =
                detailBody()?.querySelector<HTMLSelectElement>("select");
            if (!select) {
                throw new Error("boundary join select missing");
            }
            return select;
        };
        const infoText = (): string =>
            detailBody()?.querySelector<HTMLElement>(".vst-boundary-info")
                ?.textContent ?? "";

        it("renders a breadcrumb and join select for the seam", () => {
            setup([
                { duration: 4, stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(crumbText()).toBe("Boundary · Clip 0 → 1");
            expect(boundarySelect().value).toBe("cut");
        });

        it("live-applies the join mode through saveClips", () => {
            setup([
                { duration: 4, stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            const select = boundarySelect();
            select.value = "crossfade";
            select.dispatchEvent(new Event("change", { bubbles: true }));
            expect(saveSpy).toHaveBeenCalled();
            expect(savedClips(saveSpy)[0].boundaryOut).toBe("crossfade");
        });

        it("shows the 1-frame overlap note for a continue boundary", () => {
            setup([
                { duration: 4, boundaryOut: "continue", stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(infoText()).toContain("1 frame");
            expect(
                detailBody()?.querySelector(".vst-boundary-note"),
            ).not.toBeNull();
        });

        it("reports the computed crossfade overlap in frames", () => {
            setup([
                { duration: 4, boundaryOut: "crossfade", stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(infoText()).toContain("8 frames");
        });

        it("clamps a boundary selection to none when its right clip is deleted", () => {
            setup([
                { duration: 4, stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(crumbText()).toBe("Boundary · Clip 0 → 1");
            // Drop the second clip: boundary 0 no longer has a follower.
            const clips = persistence.getClips();
            clips.splice(1, 1);
            persistence.saveClips(clips);
            // A re-render re-clamps the now-invalid selection to none.
            strip?.render();
            expect(getSelection()).toEqual({ kind: "none" });
        });
    });
});
