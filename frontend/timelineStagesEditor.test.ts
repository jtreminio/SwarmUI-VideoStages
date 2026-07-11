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
import { createTimelineLinking } from "./timelineLinking";
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
    upscale?: number;
    control?: number;
    steps?: number;
}

interface ClipFixture {
    duration: number;
    stages: StageFixture[];
    refs?: { source: string; frame: number }[];
    audioSource?: string;
    clipLengthFromAudio?: boolean;
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    audioSource: clip.audioSource ?? "Native",
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

const clipInspector = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-clip-inspector");

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

const checkboxByLabel = (
    root: HTMLElement,
    label: string,
): HTMLInputElement => {
    const rows = Array.from(
        root.querySelectorAll<HTMLElement>(".vst-audio-field-check"),
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

const stageTabs = (root: HTMLElement): HTMLElement[] =>
    Array.from(
        root.querySelectorAll<HTMLElement>(
            ".vst-stage-tab:not(.vst-stage-tab-add)",
        ),
    );

const activeTabLabel = (root: HTMLElement): string | undefined =>
    root.querySelector<HTMLElement>(".vst-stage-tab-active")?.textContent ??
    undefined;

const clickTab = (root: HTMLElement, idx: number): void => {
    stageTabs(root)[idx].dispatchEvent(
        new MouseEvent("click", { bubbles: true }),
    );
};

const clickAway = (): void => {
    document.body.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
};

const escapeInspector = (insp: HTMLElement): void => {
    insp.dispatchEvent(
        new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
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

    it("opens the unified clip inspector on the clicked stage's tab", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        expect(clipInspector()).toBeNull();
        clickStageChip(body, 0, 1);
        const insp = clipInspector();
        expect(insp).not.toBeNull();
        if (!insp) {
            throw new Error("inspector missing");
        }
        expect(
            insp.querySelector(".vst-prompt-inspector-head")?.textContent,
        ).toBe("Clip 0 · 4s");
        expect(activeTabLabel(insp)).toBe("S1");
        // Clip-level fields are present.
        const labels = fieldLabels(insp);
        expect(labels).toContain("Skip this clip");
        expect(labels).toContain("Duration (s)");
    });

    it("fires onClipOpen on a plain region click, but not on shift+click or after a drag", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        const onClipOpen = jest.fn();
        const linking = createTimelineLinking({ onClipOpen });
        linking.attach(body);
        const region = body.querySelector<HTMLElement>(
            ".vst-region[data-clip-idx='0']",
        );
        if (!region) {
            throw new Error("region missing");
        }

        region.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(onClipOpen).toHaveBeenCalledTimes(1);
        expect(onClipOpen).toHaveBeenCalledWith(0, region);

        onClipOpen.mockClear();
        region.dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: true }),
        );
        expect(onClipOpen).not.toHaveBeenCalled();

        // A drag (mousedown → move past threshold → mouseup) suppresses the
        // trailing click, so onClipOpen must not fire.
        onClipOpen.mockClear();
        region.dispatchEvent(
            new MouseEvent("mousedown", { bubbles: true, clientX: 0 }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 80 }),
        );
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 80 }),
        );
        region.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(onClipOpen).not.toHaveBeenCalled();

        linking.dispose();
    });

    it("preserves edits made on another tab when switching tabs", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(body, 0, 1);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        // Edit stage 1's steps.
        const steps1 = sliderNumberByLabel(insp, "Steps");
        steps1.value = "17";
        steps1.dispatchEvent(new Event("input", { bubbles: true }));

        // Switch to stage 0 and edit its steps.
        clickTab(insp, 0);
        const steps0 = sliderNumberByLabel(insp, "Steps");
        steps0.value = "9";
        steps0.dispatchEvent(new Event("input", { bubbles: true }));

        // Switch back to stage 1: its edit survived.
        clickTab(insp, 1);
        expect(sliderNumberByLabel(insp, "Steps").value).toBe("17");

        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = savedClips(saveSpy);
        expect(clips[0].stages[0].steps).toBe(9);
        expect(clips[0].stages[1].steps).toBe(17);
    });

    it("labels the regen field exactly 'Control' and only on refine tabs", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        const labels0 = fieldLabels(insp);
        expect(labels0).not.toContain("Control");
        expect(labels0).not.toContain("Control (regen strength)");

        clickTab(insp, 1);
        const labels1 = fieldLabels(insp);
        expect(labels1).toContain("Control");
        expect(labels1).not.toContain("Control (regen strength)");
        expect(labels1).toContain("Upscale");
        expect(labels1).toContain("Upscale Method");
    });

    it("disables Upscale Method at 1× and enables it once Upscale rises above 1", () => {
        const body = setup([{ duration: 4, stages: [{}, { upscale: 1 }] }]);
        clickStageChip(body, 0, 1);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        const methodField = fieldByLabel(insp, "Upscale Method");
        const select = methodField.querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("method select missing");
        }
        expect(select.disabled).toBe(true);
        expect(methodField.classList.contains("vst-field-disabled")).toBe(true);

        const upscale = sliderNumberByLabel(insp, "Upscale");
        upscale.value = "2";
        upscale.dispatchEvent(new Event("input", { bubbles: true }));
        expect(select.disabled).toBe(false);
        expect(methodField.classList.contains("vst-field-disabled")).toBe(
            false,
        );
    });

    it("mutes the stage panel while Skip this stage is checked and persists the skip", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(body, 0, 1);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        const fields = insp.querySelector<HTMLElement>(".vst-stage-fields");
        if (!fields) {
            throw new Error("stage fields missing");
        }
        expect(fields.classList.contains("vst-stage-fields-muted")).toBe(false);

        const skip = checkboxByLabel(insp, "Skip this stage");
        skip.checked = true;
        skip.dispatchEvent(new Event("change", { bubbles: true }));
        expect(fields.classList.contains("vst-stage-fields-muted")).toBe(true);

        clickAway();
        expect(savedClips(saveSpy)[0].stages[1].skipped).toBe(true);

        // The timeline chip shows the muted (skipped) treatment.
        renderTimeline(body, savedClips(saveSpy));
        const chip = body.querySelector<HTMLElement>(
            "[data-vst-stage][data-clip-idx='0'][data-stage-idx='1']",
        );
        expect(chip?.classList.contains("vst-stage-chip-skipped")).toBe(true);
        expect(chip?.textContent).toBe("⊘ S1");
    });

    it("adds a stage via the + tab but only commits it on apply", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        insp.querySelector<HTMLElement>(".vst-stage-tab-add")?.dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(stageTabs(insp)).toHaveLength(2);
        expect(activeTabLabel(insp)).toBe("S1");
        // Nothing committed yet.
        expect(saveSpy).not.toHaveBeenCalled();

        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(2);
    });

    it("discards an in-popover stage add on Escape", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        insp.querySelector<HTMLElement>(".vst-stage-tab-add")?.dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(stageTabs(insp)).toHaveLength(2);
        escapeInspector(insp);
        expect(clipInspector()).toBeNull();
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("deletes a stage in-panel but only commits it on apply", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(body, 0, 1);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        insp.querySelector<HTMLElement>(".vst-refs-delete")?.dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(stageTabs(insp)).toHaveLength(1);
        expect(saveSpy).not.toHaveBeenCalled();

        clickAway();
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
    });

    it("hides the Delete stage button when only one stage remains", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        expect(clipInspector()?.querySelector(".vst-refs-delete")).toBeNull();
    });

    it("opens on Stage 0 with the model select focused from the model badge", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        const badge = body.querySelector<HTMLElement>("[data-vst-model]");
        badge?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        expect(activeTabLabel(insp)).toBe("S0");
        const select = fieldByLabel(
            insp,
            "Model",
        ).querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        expect(document.activeElement).toBe(select);

        select.value = "model-b.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].model).toBe(
            "model-b.safetensors",
        );
    });

    it("commits a clip Duration edit through applyClipDurationResize", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        const dur = fieldByLabel(
            insp,
            "Duration (s)",
        ).querySelector<HTMLInputElement>("input");
        if (!dur) {
            throw new Error("duration input missing");
        }
        dur.value = "6";
        dur.dispatchEvent(new Event("input", { bubbles: true }));
        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].duration).toBe(6);
    });

    it("disables the Duration field when clip length is derived from audio", () => {
        const body = setup([
            {
                duration: 4,
                audioSource: "Upload",
                clipLengthFromAudio: true,
                stages: [{}],
            },
        ]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        const field = fieldByLabel(insp, "Duration (s)");
        expect(field.querySelector<HTMLInputElement>("input")?.disabled).toBe(
            true,
        );
        expect(field.classList.contains("vst-field-disabled")).toBe(true);
    });

    it("does not save when the state token changed while the inspector was open", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        const steps = sliderNumberByLabel(insp, "Steps");
        steps.value = "12";
        steps.dispatchEvent(new Event("input", { bubbles: true }));
        // Something else mutates the carrier: the token is now stale.
        mountPromptBox("changed by someone else");
        clickAway();
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("adds and persists a LoRA row", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        insp.querySelector<HTMLElement>(".vst-stage-lora-add")?.dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(insp.querySelectorAll(".vst-stage-lora-row")).toHaveLength(1);
        clickAway();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 1 },
        ]);
    });

    it("shift+click on a stage chip deletes the stage without touching the clip", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickStageChip(body, 0, 1, true);
        expect(clipInspector()).toBeNull();
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
    });

    it("keeps the editor open for keys inside a .sui-popover but Escape elsewhere cancels", () => {
        const body = setup([{ duration: 4, stages: [{}] }]);
        clickStageChip(body, 0, 0);
        const insp = clipInspector();
        if (!insp) {
            throw new Error("inspector missing");
        }
        const popover = document.createElement("div");
        popover.className = "sui-popover";
        const search = document.createElement("input");
        popover.appendChild(search);
        insp.appendChild(popover);
        search.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        expect(clipInspector()).not.toBeNull();
        expect(saveSpy).not.toHaveBeenCalled();

        escapeInspector(insp);
        expect(clipInspector()).toBeNull();
        expect(saveSpy).not.toHaveBeenCalled();
    });
});
