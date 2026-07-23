/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";
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
import { IC_LORA_AUTO } from "./constants";
import { resetIcLoraAutoDownloads } from "./icLoraAutoDownload";
import * as persistence from "./persistence";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
} from "./selection";
import {
    createTimelineDetailStrip,
    type TimelineDetailStrip,
} from "./timelineDetailStrip";
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
    icLoras?: Record<string, unknown>[];
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
    sourceVideo?: {
        data: string;
        fileName: string;
        fps: number;
        durationSeconds: number;
        startSeconds: number;
        lengthSeconds: number;
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
    ...(clip.icLoras ? { icLoras: clip.icLoras } : {}),
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
    ...(clip.sourceVideo ? { sourceVideo: clip.sourceVideo } : {}),
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

const modelGlobals = globalThis as unknown as {
    modelsHelpers?: {
        getDataFor: (
            category: string,
            modelName: string,
        ) => {
            modelClass: { compatClass: { id: string } };
        };
    };
};

// The dock (`.vst-detail`, render-host) is a sibling of the tracks body
// (listener-host) inside the `.vst-timeline` shell, matching production.
const makeBody = (): HTMLElement => {
    const shell = document.createElement("div");
    shell.className = "vst-timeline";
    const dock = document.createElement("div");
    dock.className = "vst-detail";
    const body = document.createElement("div");
    body.className = "vst-right";
    body.id = "videostages-timeline-body";
    shell.append(dock, body);
    document.body.appendChild(shell);
    return body;
};

const dockHost = (body: HTMLElement): HTMLElement => {
    const dock = body.parentElement?.querySelector<HTMLElement>(".vst-detail");
    if (!dock) {
        throw new Error("dock host not found");
    }
    return dock;
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
            ".vst-detail-rail-list .vst-stage-tab",
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
        modelGlobals.modelsHelpers = {
            getDataFor: () => ({
                modelClass: { compatClass: { id: "ltxv2" } },
            }),
        };
        resetSelectionForTests();
        persistence.__resetPersistenceForTests();
        resetIcLoraAutoDownloads();
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
        delete modelGlobals.modelsHelpers;
        delete swarmGlobals.makeWSRequest;
        delete swarmGlobals.refreshParameterValues;
    });

    // The [AUTO] downloader reaches these SwarmUI globals; tests stub them here.
    const swarmGlobals = globalThis as unknown as {
        makeWSRequest?: jest.Mock;
        refreshParameterValues?: jest.Mock;
    };

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
        });
        // Every save commits through the store, whose notification is what
        // drives the orchestrator's timeline repaint in prod. refreshSpy
        // observes those notifications — the "timeline was repainted" signal.
        persistence.getTimelineStore().subscribe(() => refreshSpy());
        strip.attach(body, dockHost(body));
        return body;
    };

    const renderStrip = (): void => {
        if (!strip) {
            throw new Error("detail strip is not initialized");
        }
        strip.render();
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
        const labels = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .vst-audio-field-label",
            ),
        ).map((el) => el.textContent);
        expect(labels).toEqual(
            expect.arrayContaining(["Resolution", "Dimensions", "FPS"]),
        );
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
        expect(
            detail()?.querySelector(".vst-audio-tracks-panel"),
        ).not.toBeNull();
        expect(
            detail()?.querySelector(".vst-audio-tracks-planned-warning")
                ?.textContent,
        ).toBe("Planned — runtime mixer not yet connected");
    });

    it("renders the clip/stage columns when a stage chip is clicked", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickRegionStageChip(body, 0, 1);
        expect(crumbText()).toBe("Clip 1 · S1");
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

    const controlNetLabels = (): (string | null)[] =>
        Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .auto-input-name",
            ),
        ).map((el) => el.textContent);

    it("hides IC-LoRA Guide Strength when the clip has no IC-LoRAs", () => {
        setup([{ duration: 4, stages: [{}], controlNetLora: "" }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(controlNetLabels()).not.toContain("IC-LoRA Guide Strength");
    });

    it("shows IC-LoRA Guide Strength when the clip has an IC-LoRA (legacy field)", () => {
        setup([
            { duration: 4, stages: [{}], controlNetLora: "some-cnet-lora" },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(controlNetLabels()).toContain("IC-LoRA Guide Strength");
    });

    it("adds an IC-LoRA entry with defaults via the add button", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-iclora",
        );
        if (!addBtn) {
            throw new Error("add IC-LoRA button missing");
        }
        addBtn.click();
        const clips = savedClips(saveSpy);
        expect(clips[0].icLoras).toHaveLength(1);
        expect(clips[0].icLoras[0]).toEqual({
            lora: "lora-x.safetensors",
            preset: "custom",
            source: "Upload",
            stage: -1,
            strength: 1,
            attentionStrength: 1,
            controlType: "none",
            video: null,
            driveAudioRef: false,
        });
        expect(document.querySelector(".vst-detail-iclora")).not.toBeNull();
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("add IC-LoRA skips the host's (None) lora sentinel when seeding", () => {
        // The live LoRA dropdown leads with "(None)"; seeding a new entry with
        // it would make normalize drop the row on the next render.
        setup(
            [{ duration: 4, stages: [{}] }],
            ["(None)", "lora-x.safetensors"],
        );
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-iclora",
        );
        if (!addBtn) {
            throw new Error("add IC-LoRA button missing");
        }
        addBtn.click();
        const clips = savedClips(saveSpy);
        expect(clips[0].icLoras).toHaveLength(1);
        expect(clips[0].icLoras[0].lora).toBe("lora-x.safetensors");
        // The row survives the rebuild (the original bug: it vanished).
        expect(document.querySelectorAll(".vst-detail-iclora")).toHaveLength(1);
    });

    it("applying a preset seeds strength and control type", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const presetSelect =
            fieldByLabel("Preset").querySelector<HTMLSelectElement>("select");
        if (!presetSelect) {
            throw new Error("preset select missing");
        }
        presetSelect.value = "union-control";
        presetSelect.dispatchEvent(new Event("change", { bubbles: true }));
        const clips = savedClips(saveSpy);
        expect(clips[0].icLoras[0].preset).toBe("union-control");
        expect(clips[0].icLoras[0].controlType).toBe("depth");
        expect(clips[0].icLoras[0].strength).toBe(1);
    });

    it("shows the Control select only for Custom and Union Control presets", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const labels = (): (string | null)[] =>
            Array.from(
                document.querySelectorAll(
                    ".vst-detail-iclora .vst-audio-field-label",
                ),
            ).map((el) => el.textContent);
        const pickPreset = (value: string): void => {
            const select =
                fieldByLabel("Preset").querySelector<HTMLSelectElement>(
                    "select",
                );
            if (!select) {
                throw new Error("preset select missing");
            }
            select.value = value;
            select.dispatchEvent(new Event("change", { bubbles: true }));
        };
        // Custom (no preset) could be a third-party control LoRA, so Control shows.
        expect(labels()).toContain("Control");
        pickPreset("deblur");
        expect(labels()).not.toContain("Control");
        pickPreset("union-control");
        expect(labels()).toContain("Control");
    });

    // The per-entry selects render in a fixed order: Preset, LoRA, Control
    // (Custom / Union Control presets only), Apply on, then Source
    // (refine-stage placements only).
    const IC_LORA_SELECTS = ["preset", "lora", "control", "apply", "source"];
    type IcLoraSelectName = "preset" | "lora" | "apply" | "source";
    const icLoraSelect = (which: IcLoraSelectName): HTMLSelectElement => {
        const row = document.querySelector<HTMLElement>(".vst-detail-iclora");
        const select =
            row?.querySelectorAll("select")[IC_LORA_SELECTS.indexOf(which)];
        if (!select) {
            throw new Error(`IC-LoRA ${which} select missing`);
        }
        return select as HTMLSelectElement;
    };

    const changeIcLoraSelect = (
        which: IcLoraSelectName,
        value: string,
    ): void => {
        const select = icLoraSelect(which);
        select.value = value;
        select.dispatchEvent(new Event("change", { bubbles: true }));
    };

    it("Apply on lists every stage plus All stages", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const options = Array.from(icLoraSelect("apply").options).map((o) => [
            o.value,
            o.textContent,
        ]);
        expect(options).toEqual([
            ["-1", "All stages"],
            ["0", "Stage 0"],
            ["1", "Stage 1"],
        ]);
        // Source select only exists on refine-stage placements.
        expect(
            document.querySelectorAll(".vst-detail-iclora select"),
        ).toHaveLength(4);
    });

    it("refine-stage placement offers Stage input and swaps the upload row for a hint", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        changeIcLoraSelect("apply", "1");
        expect(savedClips(saveSpy)[0].icLoras[0].stage).toBe(1);

        changeIcLoraSelect("source", "Stage Input");
        const entry = savedClips(saveSpy)[0].icLoras[0];
        expect(entry.source).toBe("Stage Input");
        expect(controlNetLabels()).not.toContain("Drive Media");
        expect(detail()?.textContent).toContain("Driven by stage 1's input");
    });

    it("moving a Stage input entry off refine stages resets its source to Upload", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        source: "Stage Input",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        changeIcLoraSelect("apply", "-1");
        const entry = savedClips(saveSpy)[0].icLoras[0];
        expect(entry.stage).toBe(-1);
        expect(entry.source).toBe("Upload");
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("sourced clip renders the IC-LoRA Source select and footage-drive hint on an all-stages entry", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                sourceVideo: {
                    data: "data:video/mp4;base64,AA==",
                    fileName: "clip.mp4",
                    fps: 24,
                    durationSeconds: 4,
                    startSeconds: 0,
                    lengthSeconds: 4,
                },
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        // On a sourced clip the footage is the drive, so the Source select renders even at the
        // default All-stages placement: Preset, LoRA, Control, Apply on, Source = 5 selects.
        expect(
            document.querySelectorAll(".vst-detail-iclora select"),
        ).toHaveLength(5);
        expect(icLoraSelect("source").value).toBe("Upload");
        expect(controlNetLabels()).toContain("Drive Media");
        expect(detail()?.textContent).toContain(
            "No upload — drives from the stage's incoming footage.",
        );
    });

    it("sourced clip Stage-Input entry shows the footage-drive hint at stage 0", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                sourceVideo: {
                    data: "data:video/mp4;base64,AA==",
                    fileName: "clip.mp4",
                    fps: 24,
                    durationSeconds: 4,
                    startSeconds: 0,
                    lengthSeconds: 4,
                },
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        source: "Stage Input",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(icLoraSelect("source").value).toBe("Stage Input");
        expect(controlNetLabels()).not.toContain("Drive Media");
        expect(detail()?.textContent).toContain(
            "Driven by each stage's incoming frames (the source footage on stage 0)",
        );
    });

    it("unsourced clip omits the IC-LoRA Source select on a stage-0/all entry", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        // Preset, LoRA, Control, Apply on — no Source select without a refine-stage placement.
        expect(
            document.querySelectorAll(".vst-detail-iclora select"),
        ).toHaveLength(4);
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("leads the IC-LoRA LoRA dropdown with [AUTO]", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const options = Array.from(icLoraSelect("lora").options).map(
            (o) => o.value,
        );
        expect(options).toEqual([IC_LORA_AUTO, "lora-x.safetensors"]);
    });

    it("selecting [AUTO] with a preset starts the preset weights download", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors", preset: "deblur" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const loraSelect = icLoraSelect("lora");
        loraSelect.value = IC_LORA_AUTO;
        loraSelect.dispatchEvent(new Event("change", { bubbles: true }));

        expect(savedClips(saveSpy)[0].icLoras[0].lora).toBe(IC_LORA_AUTO);
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(1);
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledWith(
            "VideoStagesDownloadIcLoraWS",
            { presetId: "deblur" },
            expect.any(Function),
            0,
            expect.any(Function),
        );
        expect(
            document.querySelector('[data-vst-iclora-auto="deblur"]')
                ?.textContent,
        ).toContain("Downloading Deblur weights");
    });

    it("selecting a preset while on [AUTO] starts the download and refreshes on success", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        swarmGlobals.refreshParameterValues = jest.fn();
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: IC_LORA_AUTO }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(swarmGlobals.makeWSRequest).not.toHaveBeenCalled();
        expect(detail()?.textContent).toContain("[AUTO] needs a preset");

        const presetSelect = icLoraSelect("preset");
        presetSelect.value = "deblur";
        presetSelect.dispatchEvent(new Event("change", { bubbles: true }));
        expect(swarmGlobals.makeWSRequest).toHaveBeenCalledTimes(1);

        // Completion refreshes the host's model lists and settles the hint.
        const onData = swarmGlobals.makeWSRequest.mock.calls[0][2] as (
            data: Record<string, unknown>,
        ) => void;
        onData({ success: true });
        expect(swarmGlobals.refreshParameterValues).toHaveBeenCalledWith(true);
        expect(detail()?.textContent).toContain(
            "Downloaded to LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9",
        );
    });

    it("shows the transfer progress from current_percent, not the 0.2 step marker", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: IC_LORA_AUTO, preset: "deblur" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const onData = swarmGlobals.makeWSRequest.mock.calls[0][2] as (
            data: Record<string, unknown>,
        ) => void;
        // The core downloader pins overall_percent to 0.2 for the whole
        // transfer; the live percentage is current_percent.
        onData({ current_percent: 0.57, overall_percent: 0.2, per_second: 1 });
        expect(
            document.querySelector('[data-vst-iclora-auto="deblur"]')
                ?.textContent,
        ).toContain("Downloading Deblur weights… 57%");
    });

    it("skips the [AUTO] download when the preset weights are already installed", () => {
        swarmGlobals.makeWSRequest = jest.fn();
        setup(
            [
                {
                    duration: 4,
                    stages: [{}],
                    icLoras: [{ lora: IC_LORA_AUTO, preset: "deblur" }],
                },
            ],
            [
                "lora-x.safetensors",
                "LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9",
            ],
        );
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(swarmGlobals.makeWSRequest).not.toHaveBeenCalled();
        expect(detail()?.textContent).toContain(
            "Using LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9",
        );
    });

    it("offers IC-LoRAs with [AUTO] even when no LoRAs are installed", () => {
        setup([{ duration: 4, stages: [{}] }], []);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const addBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-iclora",
        );
        if (!addBtn) {
            throw new Error("add IC-LoRA button missing");
        }
        addBtn.click();
        const clips = savedClips(saveSpy);
        expect(clips[0].icLoras).toHaveLength(1);
        expect(clips[0].icLoras[0].lora).toBe(IC_LORA_AUTO);
    });

    it("removes an IC-LoRA entry via its Remove button", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const removeBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-iclora .vst-detail-instance-delete",
        );
        if (!removeBtn) {
            throw new Error("remove IC-LoRA button missing");
        }
        removeBtn.click();
        expect(savedClips(saveSpy)[0].icLoras).toHaveLength(0);
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
        expect(crumbText()).toBe("Clip 1 · S0");
        detail()?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
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
        expect(crumbText()).toBe("Clip 1 · S0");
    });

    it("shift+clicking a region stage chip deletes the stage", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickRegionStageChip(body, 0, 1, true);
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
    });

    it("remaps IC-LoRA stage targets when a stage is deleted", () => {
        const body = setup([
            {
                duration: 4,
                stages: [{}, {}, {}],
                icLoras: [
                    { lora: "a", stage: 2 },
                    { lora: "b", stage: 1, source: "Stage Input" },
                    { lora: "c", stage: 0 },
                    { lora: "d", stage: -1 },
                ],
            },
        ]);
        clickRegionStageChip(body, 0, 1, true);
        const clip = savedClips(saveSpy)[0];
        expect(clip.stages).toHaveLength(2);
        // Above the deleted stage shifts down; on it falls back to all stages
        // (which also invalidates a Stage Input source); below is untouched.
        expect(clip.icLoras.map((e) => e.stage)).toEqual([1, -1, 0, -1]);
        expect(clip.icLoras[1].source).toBe("Upload");
    });

    it("adds a stage from the rail's Add button and selects it", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        // Delete is present but DISABLED with a single stage (never hidden).
        const del = document.querySelector<HTMLButtonElement>(
            ".vst-detail-delete-stage",
        );
        expect(del).not.toBeNull();
        expect(del?.disabled).toBe(true);
        document
            .querySelector<HTMLElement>(".vst-detail-add-stage")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(2);
        expect(activeRailLabel()).toBe("S1");
        expect(crumbText()).toBe("Clip 1 · S1");
        expect(
            document.querySelector<HTMLButtonElement>(
                ".vst-detail-delete-stage",
            )?.disabled,
        ).toBe(false);
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

    describe("sourced clip stage 0 refine params", () => {
        const SOURCE_VIDEO = {
            data: "data:video/mp4;base64,AA==",
            fileName: "clip.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        const fields = (): HTMLElement | null =>
            document.querySelector<HTMLElement>(".vst-detail-fields");
        const note = (): string =>
            document.querySelector(".vst-stage-passthrough-note")
                ?.textContent ?? "";

        it("renders enabled refine params and a footage note on sourced stage 0", () => {
            setup([
                {
                    duration: 4,
                    stages: [{ control: 0.5, upscale: 2 }, {}],
                    sourceVideo: SOURCE_VIDEO,
                },
            ]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            // Sourced stage 0 refines its footage: no passthrough gating.
            expect(
                fields()?.classList.contains("vst-stage-fields-passthrough"),
            ).toBe(false);
            expect(note()).toContain("starts from the source footage");
            // The refine controls (Control / Upscale / Upscale Method) render
            // and are live — a generation stage 0 lacks them entirely.
            expect(sliderNumberByLabel("Control").disabled).toBe(false);
            expect(sliderNumberByLabel("Upscale").disabled).toBe(false);
            expect(
                fieldByLabel("Upscale Method").querySelector<HTMLSelectElement>(
                    "select",
                )?.disabled,
            ).toBe(false);

            // Later stages keep their live editors and no footage note.
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            expect(
                fields()?.classList.contains("vst-stage-fields-passthrough"),
            ).toBe(false);
            expect(note()).toBe("");
        });

        it("leaves stage 0 of an unsourced clip without refine params or note", () => {
            setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            expect(
                fields()?.classList.contains("vst-stage-fields-passthrough"),
            ).toBe(false);
            expect(note()).toBe("");
            // Generation stage 0 forces Control/Upscale, so those widgets are absent.
            expect(() => sliderNumberByLabel("Control")).toThrow();
        });
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
        expect(retake?.lengthSeconds).toBe(3); // min(default 3, clip 4)
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
        expect(crumbText()).toBe("Retake · Clip 1 · 2–5 s");
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

    it("a retake selection opens the CLIP panel with its Retake section", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        // The clip panel (bare fields + Stages group) is what renders…
        expect(detailBody()?.querySelector(".vst-detail-clip")).not.toBeNull();
        expect(
            detail()?.querySelector("#auto-group-vstdock_stages"),
        ).not.toBeNull();
        // …and its Retake section carries the editable fields.
        const retakeGroup = detail()?.querySelector(
            "#auto-group-vstdock_retake",
        );
        expect(
            retakeGroup?.querySelector(
                'input[data-vst-focus-key="retake-start"]',
            ),
        ).not.toBeNull();
        expect(crumbText()).toBe("Retake · Clip 1 · 2–5 s");
    });

    it("removes the retake and falls back to the owning clip's panel", () => {
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
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
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
        expect(crumbText()).toBe("Clip 2 · S0");

        const clips = persistence.getClips().slice(0, 1);
        persistence.saveClips(clips, { notifyDomChange: false });
        renderTimeline(body, persistence.getClips());
        strip?.render();
        expect(getSelection().kind).toBe("none");
        expect(crumbText()).toBe("Timeline settings");
    });

    const refRow = (idx: number): HTMLElement => {
        const row = document.querySelector<HTMLElement>(
            `.vst-detail-ref-row[data-vst-ref-index="${idx}"]`,
        );
        if (!row) {
            throw new Error(`ref row ${idx} missing`);
        }
        return row;
    };
    const segRow = (idx: number): HTMLElement => {
        const row = document.querySelector<HTMLElement>(
            `.vst-detail-seg-row[data-vst-seg-index="${idx}"]`,
        );
        if (!row) {
            throw new Error(`segment row ${idx} missing`);
        }
        return row;
    };

    it("stacks EVERY ref and live-applies + deletes the selected one", () => {
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
        expect(crumbText()).toBe("Ref 2 · Clip 1");
        expect(document.querySelectorAll(".vst-detail-ref-row")).toHaveLength(
            2,
        );
        expect(refRow(1).classList.contains("vst-detail-instance-active")).toBe(
            true,
        );
        expect(refRow(0).classList.contains("vst-detail-instance-active")).toBe(
            false,
        );

        // Edit is scoped to the selected ref's OWN row (index 1), not ref 0.
        const sourceSelect =
            refRow(1).querySelector<HTMLSelectElement>("select");
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
        expect(savedClips(saveSpy)[0].refs[0].source).toBe("Refiner");

        // Per-row delete removes THAT ref and re-points the selection to the
        // neighbouring ref — never back to the settings panel.
        refRow(1)
            .querySelector<HTMLElement>(".vst-detail-instance-delete")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].refs).toHaveLength(1);
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
    });

    it("deleting the LAST ref falls back to the owning clip's panel", () => {
        setup([
            {
                duration: 5,
                stages: [{}],
                refs: [{ source: "Base", frame: 1 }],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        refRow(0)
            .querySelector<HTMLElement>(".vst-detail-instance-delete")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].refs).toHaveLength(0);
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
    });

    it("re-points the selection to a ref when its row is interacted with", () => {
        setup([
            {
                duration: 5,
                stages: [{}],
                refs: [
                    { source: "Base", frame: 1 },
                    { source: "Refiner", frame: 2 },
                ],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        expect(refRow(0).classList.contains("vst-detail-instance-active")).toBe(
            true,
        );
        // Focusing a control in ref 1's row re-points the selection to ref 1 and
        // swaps the highlight WITHOUT a rebuild (rows are the same nodes).
        const before = refRow(1);
        refRow(1).querySelector<HTMLInputElement>("input")?.focus();
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });
        expect(refRow(1)).toBe(before);
        expect(refRow(1).classList.contains("vst-detail-instance-active")).toBe(
            true,
        );
        expect(refRow(0).classList.contains("vst-detail-instance-active")).toBe(
            false,
        );
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
        setup([
            {
                duration: 5,
                stages: [{}, {}, {}],
                controlNetLora: "some-lora",
            },
        ]);
        setSelection({ kind: "audio", clipIdx: 0 });
        expect(crumbText()).toBe("Audio · Clip 1");
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
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(true);

        const reuse = fieldByLabel(
            "Reuse Captured Stage Audio",
        ).querySelector<HTMLInputElement>("input");
        if (!reuse) {
            throw new Error("reuse checkbox missing");
        }
        reuse.checked = true;
        reuse.dispatchEvent(new Event("change", { bubbles: true }));
        expect(savedClips(saveSpy)[0].reuseAudio).toBe(true);

        select.value = "Upload";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(savedClips(saveSpy)[0].audioSource).toBe("Upload");
        expect(
            fieldByLabel("Clip Length from Audio").querySelector("input")
                ?.disabled,
        ).toBe(false);
    });

    it("disables captured-stage audio reuse until three stages are active", () => {
        setup([{ duration: 5, stages: [{}, {}, { skipped: true }] }]);
        setSelection({ kind: "audio", clipIdx: 0 });
        const row = fieldByLabel("Reuse Captured Stage Audio");
        const reuse = row.querySelector<HTMLInputElement>("input");
        expect(reuse?.disabled).toBe(true);
        expect(row.textContent).toContain("second active stage");
        expect(row.textContent).toContain("third active stage");
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

    it("+ Add segment appends a default-length segment on its own lane (overlap allowed)", () => {
        // Existing segments [1,3] and [4,6]; the new one may overlap them.
        setup([{ duration: 10, stages: [{}], audioSegments: twoSegments }]);
        setSelection({ kind: "audio", clipIdx: 0 });
        document
            .querySelector<HTMLElement>(".vst-detail-add-segment")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        const segments = savedClips(saveSpy)[0].audioSegments;
        expect(segments).toHaveLength(3);
        // Appended (the array index is the lane — no start-time sort): the new
        // segment starts at 0 with the default length and is selected.
        expect(segments.map((s) => s.startSeconds)).toEqual([1, 4, 0]);
        expect(segments[2].lengthSeconds).toBe(2);
        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 2,
        });
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
        expect(crumbText()).toBe("Audio segment · Clip 1 · 2–5 s");
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
        // The last segment is gone → fall back to the clip's audio panel.
        expect(getSelection()).toEqual({ kind: "audio", clipIdx: 0 });
        expect(savedClips(saveSpy)[0].audioSegments).toEqual([]);
    });

    const twoSegments = [
        {
            source: { data: "data:audio/wav;base64,QUJD", fileName: "a.wav" },
            startSeconds: 1,
            trimStartSeconds: 0,
            lengthSeconds: 2,
        },
        {
            source: { data: "data:audio/wav;base64,REVG", fileName: "b.wav" },
            startSeconds: 4,
            trimStartSeconds: 0,
            lengthSeconds: 2,
        },
    ];

    it("stacks EVERY audio segment, highlighting + re-pointing + distinct keys", () => {
        setup([{ duration: 10, stages: [{}], audioSegments: twoSegments }]);
        setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 1 });

        expect(document.querySelectorAll(".vst-detail-seg-row")).toHaveLength(
            2,
        );
        expect(segRow(1).classList.contains("vst-detail-instance-active")).toBe(
            true,
        );
        expect(segRow(0).classList.contains("vst-detail-instance-active")).toBe(
            false,
        );
        expect(
            segRow(0).querySelector(".vst-detail-instance-delete"),
        ).not.toBeNull();
        expect(
            segRow(1).querySelector(".vst-detail-instance-delete"),
        ).not.toBeNull();

        // Per-segment pending keys are distinct: editing seg 0 and seg 1 both
        // land without clobbering each other.
        jest.useFakeTimers();
        const s0 = segRow(0).querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="seg-0-start"]',
        );
        const s1 = segRow(1).querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="seg-1-start"]',
        );
        if (!s0 || !s1) {
            throw new Error("segment start inputs missing");
        }
        s0.value = "2";
        s0.dispatchEvent(new Event("input", { bubbles: true }));
        s1.value = "5";
        s1.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        const segs = savedClips(saveSpy)[0].audioSegments;
        expect(segs[0].startSeconds).toBe(2);
        expect(segs[1].startSeconds).toBe(5);
        jest.useRealTimers();
    });

    it("clamps a segment's Start/Length only to the clip bounds (overlap allowed)", () => {
        // seg0 [1,3], seg1 [4,6] in a 10s clip — segments live on their own
        // lanes, so neighbours impose no walls.
        setup([{ duration: 10, stages: [{}], audioSegments: twoSegments }]);
        setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 1 });

        const s1 = segRow(1).querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="seg-1-start"]',
        );
        expect(s1?.min).toBe("0");

        // Typing a Start INSIDE seg0's span is allowed as-is.
        jest.useFakeTimers();
        if (!s1) {
            throw new Error("start input missing");
        }
        s1.value = "2";
        s1.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        let segs = savedClips(saveSpy)[0].audioSegments;
        expect(segs[1].startSeconds).toBe(2);

        // seg0's Length may extend across seg1, clamped only at the clip end.
        setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 0 });
        const l0 = segRow(0).querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="seg-0-length"]',
        );
        if (!l0) {
            throw new Error("length input missing");
        }
        l0.value = "12";
        l0.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        segs = savedClips(saveSpy)[0].audioSegments;
        // start 1 in a 10s clip → length clamps to 9 (clip end), not seg1.
        expect(segs[0].startSeconds).toBe(1);
        expect(segs[0].lengthSeconds).toBe(9);
        jest.useRealTimers();
    });

    it("re-points the selection to a segment via a targeted highlight swap", () => {
        setup([{ duration: 10, stages: [{}], audioSegments: twoSegments }]);
        setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 0 });
        const before = segRow(1);
        // Interacting with seg 1's row re-points selection to seg 1 without a
        // rebuild (same node) and swaps the highlight.
        segRow(1).querySelector<HTMLInputElement>("input")?.focus();
        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 1,
        });
        expect(segRow(1)).toBe(before);
        expect(segRow(1).classList.contains("vst-detail-instance-active")).toBe(
            true,
        );
        expect(segRow(0).classList.contains("vst-detail-instance-active")).toBe(
            false,
        );
    });

    it("deletes one segment via its per-row delete button", () => {
        setup([{ duration: 10, stages: [{}], audioSegments: twoSegments }]);
        setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 1 });
        segRow(1)
            .querySelector<HTMLElement>(".vst-detail-instance-delete")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        const segs = savedClips(saveSpy)[0].audioSegments;
        expect(segs).toHaveLength(1);
        expect(segs[0].startSeconds).toBe(1); // the surviving first segment
        expect(getSelection()).toEqual({
            kind: "audio-segment",
            clipIdx: 0,
            segIdx: 0,
        });
    });

    it("edits the clip's major prompt (debounced) through saveClips", () => {
        setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        expect(crumbText()).toBe("Prompt · Clip 1");
        jest.useFakeTimers();
        const editor =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!editor) {
            throw new Error("prompt textarea missing");
        }
        // The panel auto-focuses the textarea, so typing is HELD (no timer)
        // while the caret is in it. Blurring out of the dock hands the field
        // back to the debounce timer, exercising the coalesced-write path.
        editor.blur();
        editor.value = "a wide landscape";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].prompt).toBe("a wide landscape");
    });

    it("auto-focuses the major prompt textarea (caret at end) on a timeline-origin selection", () => {
        setup([{ duration: 5, stages: [{}], prompt: "existing text" }]);
        // A timeline click selects the major prompt while focus is OUTSIDE the
        // dock (nothing in the dock is focused yet).
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const editor =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!editor) {
            throw new Error("prompt textarea missing");
        }
        expect(document.activeElement).toBe(editor);
        expect(editor.selectionStart).toBe(editor.value.length);
        expect(editor.selectionEnd).toBe(editor.value.length);
    });

    it("does not steal focus / snap the caret when the major prompt is re-rendered in place", () => {
        setup([{ duration: 5, stages: [{}], prompt: "existing text" }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const before =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!before) {
            throw new Error("prompt textarea missing");
        }
        // User places the caret mid-text (selection change now originates from
        // inside the dock).
        before.focus();
        before.setSelectionRange(3, 3);
        // A self-triggered re-render must preserve the caret, not snap it back
        // to the end via auto-focus.
        renderStrip();
        const after =
            document.querySelector<HTMLTextAreaElement>(".vst-detail-prompt");
        if (!after) {
            throw new Error("prompt textarea missing after render");
        }
        expect(document.activeElement).toBe(after);
        expect(after.selectionStart).toBe(3);
        expect(after.selectionEnd).toBe(3);
    });

    const minorRows = (): HTMLElement[] =>
        Array.from(
            document.querySelectorAll<HTMLElement>(".vst-detail-minor-window"),
        );
    const minorEditor = (idx: number): HTMLTextAreaElement => {
        const ta = document.querySelector<HTMLTextAreaElement>(
            `textarea[data-vst-focus-key="minor-${idx}"]`,
        );
        if (!ta) {
            throw new Error(`minor editor ${idx} missing`);
        }
        return ta;
    };

    it("lists EVERY relay window of the clip, highlighting the selected one", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 4, duration: 2, prompt: "w1" },
                    { start: 8, duration: 2, prompt: "w2" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });

        const rows = minorRows();
        expect(rows).toHaveLength(3);
        // The identity label stays W{n}; the range is now editable begin/end.
        expect(
            rows[0].querySelector(".vst-detail-minor-title")?.textContent,
        ).toBe("W1");
        expect(
            rows[2].querySelector(".vst-detail-minor-title")?.textContent,
        ).toBe("W3");
        const beginEnd = (row: HTMLElement): [string, string] => [
            row.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="-begin"]',
            )?.value ?? "",
            row.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="-end"]',
            )?.value ?? "",
        ];
        expect(beginEnd(rows[0])).toEqual(["1", "3"]);
        expect(beginEnd(rows[2])).toEqual(["8", "10"]);
        expect(rows.every((r) => r.querySelector("textarea"))).toBe(true);
        expect(rows.every((r) => r.querySelector(".vst-detail-delete"))).toBe(
            true,
        );
        expect(rows[1].classList.contains("vst-detail-minor-active")).toBe(
            true,
        );
        expect(rows[0].classList.contains("vst-detail-minor-active")).toBe(
            false,
        );
        expect(document.activeElement).toBe(minorEditor(1));
    });

    it("focusing another window's editor re-points the selection (highlight follows)", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 4, duration: 2, prompt: "w1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });

        minorEditor(1).focus();
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 1,
        });
        // And the re-render keeps focus in window 1 (not stolen back to 0).
        expect(document.activeElement).toBe(minorEditor(1));
        expect(
            minorRows()[1].classList.contains("vst-detail-minor-active"),
        ).toBe(true);
    });

    it("keeps per-window pending keys distinct so edits never collide", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "old0" },
                    { start: 4, duration: 2, prompt: "old1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        jest.useFakeTimers();

        // The selected window's editor is focused (auto-focus), so typing is
        // HELD — nothing writes mid-keystroke even past the debounce window.
        const e0 = minorEditor(0);
        e0.value = "red car";
        e0.dispatchEvent(new Event("input", { bubbles: true }));
        const e1 = minorEditor(1);
        e1.value = "blue sky";
        e1.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(saveSpy).not.toHaveBeenCalled();

        // Blurring out of the dock flushes both held edits at once, keyed
        // distinctly per window so neither clobbers the other.
        e1.dispatchEvent(
            new FocusEvent("focusout", {
                bubbles: true,
                relatedTarget: document.body,
            }),
        );
        const windows = savedClips(saveSpy)[0].promptWindows;
        expect(windows[0].prompt).toBe("red car");
        expect(windows[1].prompt).toBe("blue sky");
        jest.useRealTimers();
    });

    it("flushes a held prompt edit on a press outside the dock (timeline click)", () => {
        const body = setup([
            {
                duration: 12,
                stages: [{}],
                prompt: "old prompt",
                windows: [{ start: 1, duration: 2, prompt: "w0" }],
            },
        ]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        jest.useFakeTimers();

        // Typing in the focused major editor is HELD past the debounce window.
        const editor = document.querySelector<HTMLTextAreaElement>(
            'textarea[data-vst-focus-key="prompt-major"]',
        );
        if (!editor) {
            throw new Error("major editor missing");
        }
        editor.value = "new prompt";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(saveSpy).not.toHaveBeenCalled();

        // A press on the timeline never blurs the editor (track gestures
        // preventDefault their mousedown), but its concluding click can commit
        // a structural save that would stale-drop the held edit. The
        // document-level pointerdown must flush FIRST.
        body.dispatchEvent(new Event("pointerdown", { bubbles: true }));
        expect(savedClips(saveSpy)[0].prompt).toBe("new prompt");
        jest.useRealTimers();
    });

    it("deletes a single relay window via its per-row delete button", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "keep" },
                    { start: 4, duration: 2, prompt: "drop" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });
        minorRows()[1]
            .querySelector<HTMLElement>(".vst-detail-delete")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].promptWindows).toHaveLength(1);
        expect(savedClips(saveSpy)[0].promptWindows[0].prompt).toBe("keep");
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });
    });

    it("edits a relay window's begin/end with clamping and repaints the timeline", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" },
                    { start: 6, duration: 2, prompt: "w1" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        const beginInput = () =>
            minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-begin"]',
            );
        const endInput = () =>
            minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
        // A `change` while the number field is focused (spinner / Enter) commits
        // the held edit live.
        const commitNumber = (input: HTMLInputElement, value: string): void => {
            input.focus();
            input.value = value;
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.dispatchEvent(new Event("change", { bubbles: true }));
        };

        // Move the end out to 4s: end held-fixed rule keeps start=1, duration=3.
        const end = endInput();
        if (!end) {
            throw new Error("end input missing");
        }
        commitNumber(end, "4");
        let w0 = savedClips(saveSpy)[0].promptWindows[0];
        expect(w0.start).toBe(1);
        expect(w0.duration).toBe(3);
        // The window edit committed through the store — the notification that
        // repaints the timeline (and the on-track relay segment) in prod.
        expect(refreshSpy).toHaveBeenCalled();

        // Push begin past the neighbouring window (start 6): clamped so it can't
        // cross it — begin can't exceed end - min-duration.
        const begin = beginInput();
        if (!begin) {
            throw new Error("begin input missing");
        }
        commitNumber(begin, "9");
        w0 = savedClips(saveSpy)[0].promptWindows[0];
        // begin can't reach 9: clamped to end - PROMPT_WINDOW_MIN_DURATION
        // (4 - 0.25 = 3.75, rounded to 0.1s like the timeline gesture → 3.8) and
        // never crosses the neighbouring window at start 6.
        expect(w0.start).toBe(3.8);
        expect(w0.start).toBeLessThan(6);
    });

    it("bounds a relay window's begin/end inputs at its neighbours", () => {
        setup([
            {
                duration: 12,
                stages: [{}],
                windows: [
                    { start: 1, duration: 2, prompt: "w0" }, // [1,3]
                    { start: 6, duration: 2, prompt: "w1" }, // [6,8]
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        // w0's END spinner stops at w1's start (6): its max attr IS the wall.
        const w0End = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-0-end"]',
        );
        expect(w0End?.max).toBe("6");
        // w1's BEGIN spinner stops at w0's end (3): its min attr IS the wall.
        const w1Begin = minorRows()[1].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-1-begin"]',
        );
        expect(w1Begin?.min).toBe("3");
        // Outer edges stay clip-bounded.
        const w0Begin = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-0-begin"]',
        );
        expect(w0Begin?.min).toBe("0");
        const w1End = minorRows()[1].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-1-end"]',
        );
        expect(w1End?.max).toBe("12");
        // The attrs sit ON the 0.1 spinner grid: a 0.25-anchored min would put
        // whole-tenth values off-grid, and the browser's down-spin snap (x.95)
        // rounds half-up straight back — END could never decrease.
        expect(w0End?.min).toBe("0.3");
        expect(w0Begin?.max).toBe("11.7"); // floor(12 - 0.25) onto the grid
    });

    it("hovering a Reference Strength row highlights that ref's timeline mark", () => {
        const body = setup([
            {
                duration: 4,
                stages: [{}],
                refs: [
                    { source: "Base", frame: 1 },
                    { source: "Refiner", frame: 12 },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const rows = document.querySelectorAll<HTMLElement>(
            ".vst-detail .vst-stage-ref-slider",
        );
        expect(rows).toHaveLength(2);
        const mark = body.querySelector<HTMLElement>(
            '.vst-refs-mark[data-clip-idx="0"][data-ref-idx="1"]',
        );
        if (!mark) {
            throw new Error("ref mark missing");
        }
        rows[1].dispatchEvent(new MouseEvent("mouseenter"));
        expect(mark.classList.contains("vst-ref-hover")).toBe(true);
        expect(
            body
                .querySelector('.vst-refs-mark[data-ref-idx="0"]')
                ?.classList.contains("vst-ref-hover"),
        ).toBe(false);
        rows[1].dispatchEvent(new MouseEvent("mouseleave"));
        expect(mark.classList.contains("vst-ref-hover")).toBe(false);
    });

    // ---- value-only commits never rebuild the dock (focus survives) -------
    //
    // In production a value save commits through the store, whose notification
    // drives videoStagesTimeline.renderAll(meta) → detailStrip.render(meta)
    // SYNCHRONOUSLY. A rebuild there would innerHTML-wipe the dock and drop the
    // caret; the value primitives mark their saves valueOnly, which arrives as
    // meta.hint === "value-only" and holds the dock DOM. These tests reproduce
    // that wiring faithfully — a store subscription that calls render(meta) —
    // and assert the edited field's node (and focus) survives untouched.
    describe("value-only commits keep the dock DOM", () => {
        // Emulate the prod render trigger a save produces.
        const wireLiveRenders = (): void => {
            persistence
                .getTimelineStore()
                .subscribe((_state, meta) => strip?.render(meta));
        };
        // A spinner click / Enter: a `change` while the field still owns focus.
        const commitNumber = (input: HTMLInputElement, value: string): void => {
            input.focus();
            input.value = value;
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.dispatchEvent(new Event("change", { bubbles: true }));
        };

        it("keeps focus and the same node on a first Begin/End change, and repaints", () => {
            setup([
                {
                    duration: 12,
                    stages: [{}],
                    windows: [
                        { start: 1, duration: 2, prompt: "w0" },
                        { start: 6, duration: 2, prompt: "w1" },
                    ],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const end = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
            if (!end) {
                throw new Error("end input missing");
            }
            commitNumber(end, "4");
            // The exact same input node is still in the DOM and still focused —
            // never rebuilt out from under the caret.
            expect(
                minorRows()[0].querySelector(
                    'input[data-vst-focus-key="minor-0-end"]',
                ),
            ).toBe(end);
            expect(document.activeElement).toBe(end);
            // The data was written and the commit notification fired (the
            // timeline repaint driver).
            expect(savedClips(saveSpy)[0].promptWindows[0].duration).toBe(3);
            expect(refreshSpy).toHaveBeenCalled();
            // The value-derived breadcrumb was synced WITHOUT a rebuild.
            expect(crumbText()).toBe("Relay 1–4s · Clip 1");
        });

        it("keeps focus and the same node on a first Duration change", () => {
            setup([{ duration: 4, stages: [{}] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const dur =
                fieldByLabel("Duration (s)").querySelector<HTMLInputElement>(
                    "input",
                );
            if (!dur) {
                throw new Error("duration input missing");
            }
            commitNumber(dur, "6");
            expect(fieldByLabel("Duration (s)").querySelector("input")).toBe(
                dur,
            );
            expect(document.activeElement).toBe(dur);
            expect(savedClips(saveSpy)[0].duration).toBe(6);
        });

        it("keeps focus and the same node on a first Steps change", () => {
            setup([{ duration: 4, stages: [{ steps: 8 }] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const steps = sliderNumberByLabel("Steps");
            commitNumber(steps, "14");
            expect(sliderNumberByLabel("Steps")).toBe(steps);
            expect(document.activeElement).toBe(steps);
            expect(savedClips(saveSpy)[0].stages[0].steps).toBe(14);
        });

        it("syncs the upscale-method gate live without rebuilding the method select", () => {
            setup([{ duration: 4, stages: [{}, { upscale: 2 }] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const method =
                fieldByLabel("Upscale Method").querySelector<HTMLSelectElement>(
                    "select",
                );
            const upscale = sliderNumberByLabel("Upscale");
            expect(method?.disabled).toBe(false);
            commitNumber(upscale, "1");
            // Same select node (no rebuild) but now disabled by the live gate.
            expect(fieldByLabel("Upscale Method").querySelector("select")).toBe(
                method,
            );
            expect(method?.disabled).toBe(true);
        });

        it("still REBUILDS on a structure-affecting commit (ref source → Upload)", () => {
            setup([
                { duration: 4, stages: [{}], refs: [{ source: "", frame: 1 }] },
            ]);
            wireLiveRenders();
            setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
            const before = refRow(0).querySelector<HTMLSelectElement>("select");
            expect(detail()?.querySelector(".vst-audio-upload")).toBeNull();
            const select = refRow(0).querySelector<HTMLSelectElement>("select");
            if (!select) {
                throw new Error("ref source select missing");
            }
            select.value = "Upload";
            select.dispatchEvent(new Event("change", { bubbles: true }));
            // Structure changed: the upload row appeared and the panel rebuilt
            // (the select is a fresh node).
            expect(detail()?.querySelector(".vst-audio-upload")).not.toBeNull();
            expect(refRow(0).querySelector("select")).not.toBe(before);
        });

        it("fully rebuilds on an external (non-flush) render — the handshake never leaks", () => {
            setup([{ duration: 4, stages: [{}] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const before = fieldByLabel("Duration (s)").querySelector("input");
            // An external carrier change arriving as a plain render (no flush in
            // flight) must rebuild the dock, replacing the node.
            strip?.render();
            expect(
                fieldByLabel("Duration (s)").querySelector("input"),
            ).not.toBe(before);
        });
    });

    // ---- contextual-clamp write-back (value-only, no rebuild) -------------
    //
    // A value-only commit skips the dock rebuild that used to re-display fields.
    // For a field whose commit mutator applies a clamp its static min/max can't
    // express (a relay window's neighbour bound; a segment/retake length capped
    // by its start), the input would keep showing the raw typed value while the
    // data holds the clamped one. buildClampedNumber's readBack corrects the
    // DISPLAYED value in place after the flush — same node, focus intact, no
    // rebuild. These tests reproduce the verifier-confirmed defects.
    describe("contextual-clamp write-back", () => {
        // Faithfully reproduce the prod render trigger a value save fires, so a
        // rebuild WOULD be visible if the value-only hint leaked (the node
        // would swap).
        const wireLiveRenders = (): void => {
            persistence
                .getTimelineStore()
                .subscribe((_state, meta) => strip?.render(meta));
        };
        // A spinner click / Enter: a `change` while the field still owns focus.
        const commitNumber = (input: HTMLInputElement, value: string): void => {
            input.focus();
            input.value = value;
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.dispatchEvent(new Event("change", { bubbles: true }));
        };

        it("re-displays a relay Begin clamped by the neighbouring window", () => {
            setup([
                {
                    duration: 10,
                    stages: [{}],
                    windows: [
                        { start: 0, duration: 3, prompt: "w0" },
                        { start: 5, duration: 3, prompt: "w1" },
                    ],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 1 });
            const begin = minorRows()[1].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-1-begin"]',
            );
            if (!begin) {
                throw new Error("begin input missing");
            }
            commitNumber(begin, "1");
            // Stored begin clamped to the neighbour bound (W0 ends at 3); the
            // input is corrected to the stored value, not the typed 1.
            expect(savedClips(saveSpy)[0].promptWindows[1].start).toBe(3);
            const after = minorRows()[1].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-1-begin"]',
            );
            expect(after).toBe(begin); // same node — no rebuild
            expect(after?.value).toBe("3"); // shows the clamped stored value
            expect(document.activeElement).toBe(begin); // focus intact
            expect(crumbText()).toBe("Relay 3–8s · Clip 1");
        });

        it("re-displays a relay End clamped to the minimum duration", () => {
            setup([
                {
                    duration: 10,
                    stages: [{}],
                    windows: [{ start: 5, duration: 3, prompt: "w0" }],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const end = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
            if (!end) {
                throw new Error("end input missing");
            }
            commitNumber(end, "0.3");
            // End can't come inside start + min-duration (5 + 0.25 = 5.25); the
            // gesture rounds seconds to 0.1 (roundSeconds: Math.round(2.5)=3), so
            // the stored end is 5.3 — either way, NOT the typed 0.3.
            const w = savedClips(saveSpy)[0].promptWindows[0];
            expect(w.start).toBe(5);
            expect(w.duration).toBe(0.3);
            const after = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-0-end"]',
            );
            expect(after).toBe(end);
            expect(after?.value).toBe("5.3");
            expect(document.activeElement).toBe(end);
        });

        it("re-displays an audio-segment Length capped by its start", () => {
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
                            startSeconds: 8,
                            trimStartSeconds: 0,
                            lengthSeconds: 1,
                        },
                    ],
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "audio-segment", clipIdx: 0, segIdx: 0 });
            const len = fieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="seg-0-length"]',
            );
            if (!len) {
                throw new Error("length input missing");
            }
            commitNumber(len, "5");
            // Length capped at clipDur - start = 10 - 8 = 2.
            expect(savedClips(saveSpy)[0].audioSegments[0].lengthSeconds).toBe(
                2,
            );
            const after = fieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="seg-0-length"]',
            );
            expect(after).toBe(len);
            expect(after?.value).toBe("2");
            expect(document.activeElement).toBe(len);
        });

        it("re-displays a retake Length capped by its start", () => {
            setup([
                {
                    duration: 10,
                    stages: [{}],
                    retake: {
                        startSeconds: 8,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "retake", clipIdx: 0 });
            const len = fieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="retake-length"]',
            );
            if (!len) {
                throw new Error("retake length input missing");
            }
            commitNumber(len, "5");
            expect(savedClips(saveSpy)[0].retake?.lengthSeconds).toBe(2);
            const after = fieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="retake-length"]',
            );
            expect(after).toBe(len);
            expect(after?.value).toBe("2");
            expect(document.activeElement).toBe(len);
        });

        it("does NOT write back mid-typing (no flush, no clamp, keeps typed text)", () => {
            setup([
                {
                    duration: 10,
                    stages: [{}],
                    retake: {
                        startSeconds: 8,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "retake", clipIdx: 0 });
            jest.useFakeTimers();
            const len = fieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="retake-length"]',
            );
            if (!len) {
                throw new Error("retake length input missing");
            }
            len.focus();
            len.value = "5";
            // Only an `input` event (still typing): the flush is HELD while the
            // number field owns focus, so no save and no write-back fire.
            len.dispatchEvent(new Event("input", { bubbles: true }));
            expect(saveSpy).not.toHaveBeenCalled();
            expect(len.value).toBe("5"); // typed text untouched
            jest.advanceTimersByTime(200);
            // The debounce timer was never armed (typing deferral), so still no
            // save until a blur/change flush.
            expect(saveSpy).not.toHaveBeenCalled();
            expect(len.value).toBe("5");
        });

        it("does NOT rewrite a non-clamped field (Steps) that was already valid", () => {
            setup([{ duration: 4, stages: [{ steps: 8 }] }]);
            wireLiveRenders();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const steps = sliderNumberByLabel("Steps");
            commitNumber(steps, "14");
            // Steps has no readBack, so nothing forces its display — it keeps the
            // value the user committed (in range), same node, focus intact.
            expect(sliderNumberByLabel("Steps")).toBe(steps);
            expect(steps.value).toBe("14");
            expect(savedClips(saveSpy)[0].stages[0].steps).toBe(14);
            expect(document.activeElement).toBe(steps);
        });
    });

    it("renders both prompt panels as headerless groups (breadcrumb is the label)", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                windows: [{ start: 2, duration: 3, prompt: "x" }],
            },
        ]);

        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const major = detail()?.querySelector<HTMLElement>(
            "#auto-group-vstdock_promptmajor",
        );
        expect(major?.querySelector(".input-group-header")).toBeNull();
        expect(major?.querySelector(".header-label")).toBeNull();
        expect(major?.classList.contains("input-group-open")).toBe(true);
        expect(
            major?.querySelector<HTMLElement>(".input-group-content")?.style
                .display,
        ).not.toBe("none");
        // The panel's one label is the breadcrumb.
        expect(crumbText()).toBe("Prompt · Clip 1");

        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        const minor = detail()?.querySelector<HTMLElement>(
            "#auto-group-vstdock_promptminor",
        );
        expect(minor?.querySelector(".input-group-header")).toBeNull();
        expect(minor?.querySelector(".header-label")).toBeNull();
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

    describe("slider drag", () => {
        const rangeByLabel = (label: string): HTMLInputElement => {
            const box = Array.from(
                document.querySelectorAll<HTMLElement>(
                    ".vst-detail .vst-stage-slider",
                ),
            ).find(
                (el) =>
                    el.querySelector(".auto-input-name")?.textContent === label,
            );
            const input = box?.querySelector<HTMLInputElement>(
                "input.auto-slider-range",
            );
            if (!input) {
                throw new Error(`range not found: ${label}`);
            }
            return input;
        };

        // jsdom has no PointerEvent constructor; a bubbling generic Event with
        // the pointer type name reaches the document-level capture listeners and
        // carries the range as its target, which is all the latch reads.
        const pointer = (el: Element, type: string): void => {
            el.dispatchEvent(new Event(type, { bubbles: true }));
        };

        const refClip = (): ClipFixture => ({
            duration: 4,
            stages: [{ steps: 8 }],
            refs: [{ source: "Base", frame: 1 }],
        });

        it("holds the debounced edit through a drag (no mid-drag save or rebuild)", () => {
            setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const range = rangeByLabel("R0");
            // pointerdown latches the drag; streamed inputs sync range → number
            // → our onChange (host enableSliderForBox wiring is live in tests).
            pointer(range, "pointerdown");
            for (const v of ["0.8", "0.6", "0.4"]) {
                range.value = v;
                range.dispatchEvent(new Event("input", { bubbles: true }));
            }
            // Well past the 200ms window: nothing is written and the range node
            // is NOT rebuilt out from under the drag gesture.
            jest.advanceTimersByTime(1000);
            expect(saveSpy).not.toHaveBeenCalled();
            expect(rangeByLabel("R0")).toBe(range);
            jest.useRealTimers();
        });

        it("commits exactly once on pointer release", () => {
            setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const range = rangeByLabel("R0");
            pointer(range, "pointerdown");
            range.value = "0.4";
            range.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(saveSpy).not.toHaveBeenCalled();
            // Release flushes the held edit exactly once (one save, one repaint).
            pointer(range, "pointerup");
            expect(saveSpy).toHaveBeenCalledTimes(1);
            expect(savedClips(saveSpy)[0].stages[0].refStrengths[0]).toBe(0.4);
            jest.advanceTimersByTime(1000);
            expect(saveSpy).toHaveBeenCalledTimes(1);
            jest.useRealTimers();
        });

        it("clears the latch on pointercancel (no stray hold afterward)", () => {
            setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const range = rangeByLabel("R0");
            pointer(range, "pointerdown");
            range.value = "0.4";
            range.dispatchEvent(new Event("input", { bubbles: true }));
            // Cancel clears the latch and flushes the queued edit.
            pointer(range, "pointercancel");
            expect(saveSpy).toHaveBeenCalledTimes(1);
            // With the latch cleared, a subsequent unfocused (non-gesture) slider
            // edit resumes its normal debounced flush — proving the latch is not
            // stuck set (an input while nothing in the dock has focus arms the
            // timer, which would stay held if sliderDragActive were still true).
            const steps = sliderNumberByLabel("Steps");
            steps.value = "12";
            steps.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(200);
            expect(saveSpy).toHaveBeenCalledTimes(2);
            jest.useRealTimers();
        });

        it("removes the document-level pointer listeners on dispose", () => {
            setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const removeSpy = jest.spyOn(document, "removeEventListener");
            strip?.dispose();
            strip = null;
            const removed = removeSpy.mock.calls
                .filter((c) => c[2] === true)
                .map((c) => c[0]);
            expect(removed).toContain("pointerdown");
            expect(removed).toContain("pointerup");
            expect(removed).toContain("pointercancel");
        });
    });

    describe("dock groups & collapse", () => {
        const group = (key: string): HTMLElement | null =>
            detail()?.querySelector<HTMLElement>(`#auto-group-${key}`) ?? null;

        it("renders bare clip fields plus ONE Stages group (rail + parameters)", () => {
            setup([{ duration: 4, stages: [{}, {}, {}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });

            // The clip's own fields render directly in the body — no group
            // container, no header (the breadcrumb already names the clip).
            expect(group("vstdock_clip")).toBeNull();
            const clipCol = detailBody()?.querySelector(".vst-detail-clip");
            expect(clipCol).not.toBeNull();
            expect(clipCol?.closest(".input-group")).toBeNull();

            // One merged Stages group: a plain "Stages" label, the selector
            // rail, and the selected stage's parameters — one section.
            expect(group("vstdock_stageparams")).toBeNull();
            const stages = group("vstdock_stages");
            expect(stages).not.toBeNull();
            expect(
                stages?.querySelector("#input_group_content_vstdock_stages"),
            ).not.toBeNull();
            const wrap = stages?.querySelector(".vst-detail-stages-wrap");
            expect(wrap).not.toBeNull();
            const cols = wrap ? Array.from(wrap.children) : [];
            expect(cols[0]?.classList.contains("vst-detail-sec")).toBe(true);
            expect(cols[0]?.textContent).toBe("Stages");
            expect(cols[1]?.classList.contains("vst-detail-rail")).toBe(true);
            expect(cols[2]?.classList.contains("vst-detail-params")).toBe(true);
            expect(
                wrap?.querySelector(".vst-detail-params .vst-stage-loras"),
            ).not.toBeNull();

            // The Retake section lives in the clip panel too, labelled, with
            // an add button when the clip has no retake.
            const retake = group("vstdock_retake");
            expect(retake).not.toBeNull();
            const retakeSec =
                retake?.querySelector<HTMLElement>(".vst-detail-sec");
            // The label text is the section's leading text node; a trailing "?"
            // help button now shares the element, so read the text node itself.
            expect(retakeSec?.firstChild?.textContent).toBe("Retake");
            expect(
                retakeSec?.querySelector(".info-popover-button"),
            ).not.toBeNull();
            expect(
                retake?.querySelector(".vst-detail-add-retake"),
            ).not.toBeNull();
        });

        it("wraps a single-panel selection in one keyed headerless group", () => {
            setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "audio", clipIdx: 0 });
            const audio = group("vstdock_audio");
            expect(audio).not.toBeNull();
            // The breadcrumb is the panel's one label; the group has no header.
            expect(crumbText()).toBe("Audio · Clip 1");
            expect(audio?.querySelector(".input-group-header")).toBeNull();
            expect(detail()?.querySelectorAll(".input-group")).toHaveLength(1);
        });

        it("renders every group headerless, always open, cookies ignored", () => {
            // Even a stale group_open_*=closed cookie must not collapse
            // anything: per-section headers and minimize are gone entirely.
            const globals = globalThis as unknown as {
                getCookie: (name: string) => string;
            };
            expect(typeof globals.getCookie).toBe("function");
            jest.spyOn(globals, "getCookie").mockImplementation(() => "closed");
            setup([{ duration: 4, stages: [{}] }]);
            // Default selection is none → the settings group.
            const settings = group("vstdock_settings");
            expect(settings?.querySelector(".input-group-header")).toBeNull();
            expect(settings?.querySelector(".auto-symbol")).toBeNull();
            expect(settings?.classList.contains("input-group-open")).toBe(true);
            expect(settings?.classList.contains("input-group-closed")).toBe(
                false,
            );
            const content = settings?.querySelector<HTMLElement>(
                ".input-group-content",
            );
            expect(content?.style.display).not.toBe("none");
        });

        it("width-collapses the dock via the header chevron", () => {
            setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            expect(detail()?.classList.contains("vst-detail-collapsed")).toBe(
                false,
            );
            detail()
                ?.querySelector<HTMLElement>(".vst-detail-collapse")
                ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
            expect(detail()?.classList.contains("vst-detail-collapsed")).toBe(
                true,
            );
            expect(detailBody()).toBeNull();
        });
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
        const overlapSelect = (): HTMLSelectElement | null => {
            const fields = Array.from(
                detailBody()?.querySelectorAll<HTMLElement>(
                    ".vst-audio-field",
                ) ?? [],
            );
            const field = fields.find(
                (f) =>
                    f.querySelector(".vst-audio-field-label")?.textContent ===
                    "Overlap",
            );
            return field?.querySelector<HTMLSelectElement>("select") ?? null;
        };

        it("renders a breadcrumb and join select for the seam", () => {
            setup([
                { duration: 4, stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(crumbText()).toBe("Boundary · Clip 1 → 2");
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

        it("shows an Overlap selector and plan-aware info for a continue boundary", () => {
            setup([
                { duration: 4, boundaryOut: "continue", stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            // Default overlap 8 -> window 9 for ample clips.
            expect(infoText()).toContain("last 9 frames");
            expect(overlapSelect()).not.toBeNull();
            expect(overlapSelect()?.value).toBe("8");
        });

        it("commits a chosen overlap to boundaryOutOverlap", () => {
            setup([
                { duration: 4, boundaryOut: "continue", stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            const select = overlapSelect();
            if (!select) {
                throw new Error("overlap select missing");
            }
            select.value = "24";
            select.dispatchEvent(new Event("change", { bubbles: true }));
            expect(savedClips(saveSpy)[0].boundaryOutOverlap).toBe(24);
        });

        it("shows an Overlap selector and dissolve info for a crossfade boundary", () => {
            setup([
                { duration: 4, boundaryOut: "crossfade", stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(overlapSelect()).not.toBeNull();
            expect(overlapSelect()?.value).toBe("8");
            expect(
                detailBody()?.querySelector(".vst-boundary-note"),
            ).toBeNull();
            expect(infoText()).toContain("8 frames");
        });

        it("shows no Overlap selector and no LTX-2 note for a cut boundary", () => {
            setup([
                { duration: 4, boundaryOut: "cut", stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(overlapSelect()).toBeNull();
            expect(
                detailBody()?.querySelector(".vst-boundary-note"),
            ).toBeNull();
        });

        it("clamps a boundary selection to none when its right clip is deleted", () => {
            setup([
                { duration: 4, stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(crumbText()).toBe("Boundary · Clip 1 → 2");
            // Drop the second clip: boundary 0 no longer has a follower.
            const clips = persistence.getClips();
            clips.splice(1, 1);
            persistence.saveClips(clips);
            // A re-render re-clamps the now-invalid selection to none.
            strip?.render();
            expect(getSelection()).toEqual({ kind: "none" });
        });
    });

    describe("defer-while-typing", () => {
        const blurOutOfDock = (el: HTMLElement): void => {
            el.dispatchEvent(
                new FocusEvent("focusout", {
                    bubbles: true,
                    relatedTarget: document.body,
                }),
            );
        };

        it("holds a major-prompt edit while focused and flushes on blur out", () => {
            setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            jest.useFakeTimers();
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            if (!editor) {
                throw new Error("prompt textarea missing");
            }
            editor.focus();
            editor.value = "typed while focused";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(saveSpy).not.toHaveBeenCalled();
            blurOutOfDock(editor);
            expect(saveSpy).toHaveBeenCalledTimes(1);
            expect(savedClips(saveSpy)[0].prompt).toBe("typed while focused");
            jest.useRealTimers();
        });

        it("keeps holding when focus moves to another dock field, not out", () => {
            setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            jest.useFakeTimers();
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            const sibling = document.querySelector<HTMLElement>(
                ".vst-detail-collapse",
            );
            if (!editor || !sibling) {
                throw new Error("dock nodes missing");
            }
            editor.focus();
            editor.value = "held";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            // Focus moving to another element still INSIDE the dock keeps the
            // edit held (relatedTarget is in the dock).
            editor.dispatchEvent(
                new FocusEvent("focusout", {
                    bubbles: true,
                    relatedTarget: sibling,
                }),
            );
            expect(saveSpy).not.toHaveBeenCalled();
            jest.useRealTimers();
        });

        it("commits a number spinner change live even while the field is focused", () => {
            setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const dur =
                fieldByLabel("Duration (s)").querySelector<HTMLInputElement>(
                    "input",
                );
            if (!dur) {
                throw new Error("duration input missing");
            }
            dur.focus();
            // Typing is held (no timer) while focused...
            dur.value = "7";
            dur.dispatchEvent(new Event("input", { bubbles: true }));
            jest.advanceTimersByTime(1000);
            expect(saveSpy).not.toHaveBeenCalled();
            // ...but a `change` while still focused (spinner/Enter) commits live.
            dur.dispatchEvent(new Event("change", { bubbles: true }));
            expect(saveSpy).toHaveBeenCalled();
            expect(savedClips(saveSpy)[0].duration).toBe(7);
            jest.useRealTimers();
        });

        it("does NOT force focus back into a textarea the user tabbed away from", () => {
            setup([{ duration: 5, stages: [{}], prompt: "existing" }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            if (!editor) {
                throw new Error("prompt textarea missing");
            }
            editor.focus();
            editor.value = "edited then left";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            blurOutOfDock(editor);
            // A later refresh/render must NOT yank focus back into the prompt.
            renderStrip();
            const after =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            expect(document.activeElement).not.toBe(editor); // old node gone
            expect(document.activeElement).not.toBe(after); // not re-grabbed
        });

        it("preserves focus on the NEW dock field when focus moves within the dock", () => {
            setup([
                {
                    duration: 12,
                    stages: [{}],
                    windows: [
                        { start: 1, duration: 2, prompt: "w0" },
                        { start: 5, duration: 2, prompt: "w1" },
                    ],
                },
            ]);
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const e0 = document.querySelector<HTMLTextAreaElement>(
                'textarea[data-vst-focus-key="minor-0"]',
            );
            const e1 = document.querySelector<HTMLTextAreaElement>(
                'textarea[data-vst-focus-key="minor-1"]',
            );
            if (!e0 || !e1) {
                throw new Error("relay editors missing");
            }
            e0.focus();
            e0.value = "typing in zero";
            e0.dispatchEvent(new Event("input", { bubbles: true }));
            e0.dispatchEvent(
                new FocusEvent("focusout", {
                    bubbles: true,
                    relatedTarget: e1,
                }),
            );
            e1.focus();
            // A render keeps focus on the field the user moved TO, never the one
            // they left.
            renderStrip();
            const e1After = document.querySelector<HTMLTextAreaElement>(
                'textarea[data-vst-focus-key="minor-1"]',
            );
            expect(document.activeElement).toBe(e1After);
        });

        it("flushes the held edit before a subsequent carrier read (Generate ordering)", () => {
            setup([{ duration: 5, stages: [{}] }]);
            setSelection({ kind: "prompt-major", clipIdx: 0 });
            const editor =
                document.querySelector<HTMLTextAreaElement>(
                    ".vst-detail-prompt",
                );
            if (!editor) {
                throw new Error("prompt textarea missing");
            }
            editor.focus();
            editor.value = "landscape at dusk";
            editor.dispatchEvent(new Event("input", { bubbles: true }));
            // Simulate the exact ordering a Generate click produces: focus leaves
            // the dock (focusout) BEFORE anything reads the carrier.
            let promptAtReadTime: string | null = null;
            const readCarrier = (): void => {
                promptAtReadTime =
                    document.querySelector<HTMLInputElement>("#input_prompt")
                        ?.value ?? null;
            };
            blurOutOfDock(editor);
            readCarrier();
            expect(saveSpy).toHaveBeenCalledTimes(1);
            expect(promptAtReadTime).toContain("landscape at dusk");
        });
    });

    describe("scroll + targeted updates", () => {
        it("preserves dock-body scrollTop across a value-change render", () => {
            setup([{ duration: 4, stages: [{}, {}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const body = detailBody();
            if (!body) {
                throw new Error("dock body missing");
            }
            body.scrollTop = 140;
            // A full re-render rebuilds .vst-detail-body's innerHTML.
            strip?.render();
            const rebuilt = detailBody();
            expect(rebuilt).not.toBe(body); // proves a rebuild happened
            expect(rebuilt?.scrollTop).toBe(140); // ...yet scroll is preserved
        });

        it("moves relay selection within the panel without a rebuild, keeping the caret", () => {
            setup([
                {
                    duration: 12,
                    stages: [{}],
                    windows: [
                        { start: 1, duration: 2, prompt: "hello world" },
                        { start: 4, duration: 2, prompt: "second window" },
                    ],
                },
            ]);
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            const e1Before = minorEditor(1);
            // Clicking into window 1's editor: focus fires the re-point, which
            // does a targeted highlight swap (no rebuild) — so the node is stable
            // and the caret the user placed is NOT snapped to the end.
            e1Before.focus();
            e1Before.setSelectionRange(3, 3);
            expect(getSelection()).toEqual({
                kind: "prompt-minor",
                clipIdx: 0,
                windowIdx: 1,
            });
            expect(minorEditor(1)).toBe(e1Before); // same node — no rebuild
            expect(document.activeElement).toBe(e1Before);
            expect(e1Before.selectionStart).toBe(3); // caret preserved
            expect(
                minorRows()[1].classList.contains("vst-detail-minor-active"),
            ).toBe(true);
        });
    });

    // ---- #2: native-widget markup + dock-override CSSOM probe -------------
    // The refactor swapped the dock's custom field markup for SwarmUI's native
    // `.auto-input` widget vocabulary, so HOST site.css styles the controls and
    // only a short, native-class-keyed override remains in our sheet. This probe
    // is the regression net that would have caught all three prior CSS rounds:
    // it loads the real host site.css alongside ours and asserts, on the actual
    // rendered rows, that (a) rows carry the native host classes, (b) our few
    // `.vst-detail` overrides resolve, and (c) with both sheets cascading, the
    // controls read full-width and left-aligned (never narrow or centered).

    describe("native widget markup + dock override (CSSOM probe)", () => {
        // The main checkout reaches host wwwroot at ../../../wwwroot; inside a
        // git worktree the extension is nested deeper, so walk up for it.
        const wwwrootDir = ((): string => {
            let dir = __dirname;
            for (;;) {
                const candidate = path.join(dir, "wwwroot");
                if (fs.existsSync(candidate)) {
                    return candidate;
                }
                const parent = path.dirname(dir);
                if (parent === dir) {
                    return path.resolve(__dirname, "..", "..", "..", "wwwroot");
                }
                dir = parent;
            }
        })();
        const injectCss = (id: string, filePath: string): void => {
            const css = fs.readFileSync(filePath, "utf8");
            const style = document.createElement("style");
            style.id = id;
            style.textContent = css;
            document.head.appendChild(style);
        };
        // Host site.css + the default theme (modern_dark = modern.css + vars)
        // FIRST, our dock sheet SECOND — mirrors production load order and lets
        // our higher-specificity `.vst-detail` overrides win. The theme MUST be
        // in the probe: modern.css styles `.input-group .input-group-content>div`
        // (padding + align-items:center) at a specificity that beat our
        // single-class wrappers undetected until it was probed.
        const injectHostCss = (): void => {
            injectCss(
                "vst-probe-host-css",
                path.join(wwwrootDir, "css", "site.css"),
            );
            injectCss(
                "vst-probe-theme-css",
                path.join(wwwrootDir, "css", "themes", "modern.css"),
            );
        };
        const injectDockCss = (): void =>
            injectCss(
                "vst-probe-css",
                path.join(__dirname, "..", "Assets", "video-stages.css"),
            );

        const computed = (el: Element): CSSStyleDeclaration =>
            window.getComputedStyle(el);

        beforeEach(() => {
            setup([
                {
                    duration: 10,
                    stages: [{}, {}],
                    refs: [{ source: "Base", frame: 1 }],
                    windows: [{ start: 1, duration: 2, prompt: "w" }],
                    audioSegments: [
                        {
                            source: {
                                data: "data:audio/wav;base64,QUJD",
                                fileName: "a.wav",
                            },
                            startSeconds: 1,
                            trimStartSeconds: 0,
                            lengthSeconds: 2,
                        },
                    ],
                },
            ]);
        });

        it("(a) emits native SwarmUI `.auto-input` widget markup for every field type", () => {
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const modelSelect = fieldByLabel("Model").querySelector("select");
            expect(modelSelect?.classList.contains("auto-dropdown")).toBe(true);
            const modelRow = fieldByLabel("Model");
            expect(modelRow.classList.contains("auto-input")).toBe(true);
            expect(modelRow.classList.contains("auto-dropdown-box")).toBe(true);
            expect(
                modelRow.querySelector(".auto-input-name")?.textContent,
            ).toBe("Model");
            const durInput =
                fieldByLabel("Duration (s)").querySelector("input");
            expect(durInput?.classList.contains("auto-number")).toBe(true);
            expect(
                fieldByLabel("Duration (s)").classList.contains(
                    "auto-number-box",
                ),
            ).toBe(true);
            const skipRow = document.querySelector<HTMLElement>(
                ".vst-detail .vst-audio-field-check",
            );
            expect(skipRow?.classList.contains("auto-checkbox-box")).toBe(true);
            expect(
                skipRow
                    ?.querySelector("input")
                    ?.classList.contains("auto-checkbox"),
            ).toBe(true);

            setSelection({ kind: "prompt-major", clipIdx: 0 });
            const editor = document.querySelector<HTMLTextAreaElement>(
                ".vst-detail .vst-prompt-editor",
            );
            expect(editor?.classList.contains("auto-text")).toBe(true);
            expect(editor?.classList.contains("auto-text-block")).toBe(true);
        });

        it("(b) resolves the dock's native-class overrides (label left, controls full-width)", () => {
            injectDockCss();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            // Label typography override: host centers `.auto-input-name`; the
            // dock reads left.
            const name = document.querySelector(".vst-detail .auto-input-name");
            expect(name).not.toBeNull();
            if (name) {
                expect(computed(name).textAlign).toBe("left");
            }
            for (const control of document.querySelectorAll(
                ".vst-detail .auto-dropdown, .vst-detail .auto-number",
            )) {
                expect(computed(control).width).toBe("100%");
            }
            const check = document.querySelector(
                ".vst-detail .auto-checkbox-box",
            );
            expect(check).not.toBeNull();
            if (check) {
                expect(computed(check).alignItems).toBe("center");
            }
        });

        it("(d) every ancestor from group-content down to the prompt textarea declares full-width/stretch", () => {
            injectHostCss();
            injectDockCss();
            // A node passes if it will fill its parent's width: an explicit
            // width:100%, a plain block box, OR a flex box that stretches its
            // children (align-items stretch/normal). A regression that shrink-
            // wraps a parent (inline-block, width:auto/fit-content, align-items
            // center/flex-start) fails this. The textarea itself must be a
            // full-width block, never the default inline-block.
            const stretches = (el: Element): boolean => {
                const c = computed(el);
                if (c.width === "100%") {
                    return true;
                }
                if (c.display === "block") {
                    return true;
                }
                return (
                    c.display.includes("flex") &&
                    ["", "normal", "stretch"].includes(c.alignItems)
                );
            };
            const walkFromTextarea = (): void => {
                const ta = document.querySelector<HTMLElement>(
                    ".vst-detail .vst-detail-prompt",
                );
                expect(ta).not.toBeNull();
                if (!ta) {
                    return;
                }
                expect(computed(ta).width).toBe("100%");
                expect(computed(ta).display).not.toBe("inline");
                expect(computed(ta).display).not.toBe("inline-block");
                // Walk up to (and including) the host group content: every link
                // must keep the width, or the 100% resolves against a shrunk box.
                let node: HTMLElement | null = ta.parentElement;
                let sawGroupContent = false;
                while (node) {
                    expect({
                        node: node.className,
                        stretches: stretches(node),
                    }).toEqual({ node: node.className, stretches: true });
                    if (node.classList.contains("input-group-content")) {
                        sawGroupContent = true;
                        break;
                    }
                    node = node.parentElement;
                }
                expect(sawGroupContent).toBe(true);
            };

            setSelection({ kind: "prompt-major", clipIdx: 0 });
            walkFromTextarea();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            walkFromTextarea();
        });

        it("(e) out-specifies the theme's content>div center rule — sections stretch", () => {
            // themes/modern.css ships `.input-group .input-group-content>div
            // { padding: 5px 10px; align-items: center }`, which center-shrinks
            // every dock section wrapper unless our higher-specificity counter
            // rule wins.
            injectHostCss();
            injectDockCss();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const wraps = document.querySelectorAll<HTMLElement>(
                ".vst-detail .input-group-content > div",
            );
            expect(wraps.length).toBeGreaterThan(0);
            for (const wrap of wraps) {
                expect({
                    node: wrap.className,
                    alignItems: computed(wrap).alignItems,
                }).toEqual({ node: wrap.className, alignItems: "stretch" });
            }
        });

        it("(c) with HOST site.css cascading, prompt/select/number rows read full-width, never centered", () => {
            injectHostCss();
            injectDockCss();
            const panels: (() => void)[] = [
                () => setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 }),
                () => setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 }),
                () => setSelection({ kind: "audio", clipIdx: 0 }),
                () =>
                    setSelection({
                        kind: "audio-segment",
                        clipIdx: 0,
                        segIdx: 0,
                    }),
                () => setSelection({ kind: "prompt-major", clipIdx: 0 }),
                () =>
                    setSelection({
                        kind: "prompt-minor",
                        clipIdx: 0,
                        windowIdx: 0,
                    }),
            ];
            for (const select of panels) {
                select();
                // Every field row + its label reads left (no host centering
                // leaks through), across every panel.
                for (const field of document.querySelectorAll(
                    ".vst-detail .auto-input",
                )) {
                    expect(computed(field).textAlign).toBe("left");
                }
                for (const nm of document.querySelectorAll(
                    ".vst-detail .auto-input:not(.auto-checkbox-box) .auto-input-name",
                )) {
                    expect(computed(nm).textAlign).toBe("left");
                }
                // Native textareas / dropdowns / numbers + the upload box fill
                // the dock column — the round-3 "not full width" symptom.
                for (const control of document.querySelectorAll(
                    [
                        ".vst-detail .auto-text",
                        ".vst-detail .auto-dropdown",
                        ".vst-detail .auto-number",
                        ".vst-detail .vst-audio-upload",
                    ].join(", "),
                )) {
                    expect(computed(control).width).toBe("100%");
                }
            }
        });
    });
});
