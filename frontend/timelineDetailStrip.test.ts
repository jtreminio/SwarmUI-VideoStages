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
    mountVideoFps,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import {
    IC_LORA_AUTO,
    resetIcLoraAutoDownloads,
} from "./architectures/ltx2/icLoraAutoDownload";
import * as persistence from "./persistence";
import {
    activateSelection,
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
    skipped?: boolean;
    stages: StageFixture[];
    refs?: { source: string; frame: number }[];
    audioSource?: string;
    uploadedAudio?: { data: string; fileName: string };
    controlNetLora?: string;
    icLoras?: Record<string, unknown>[];
    reuseAudio?: boolean;
    clipLengthFromAudio?: boolean;
    prompt?: string;
    windows?: WindowFixture[];
    boundaryOut?: "cut" | "continue" | "crossfade";
    boundaryOutCarryAudio?: boolean;
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
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    skipped: clip.skipped ?? false,
    boundaryOut: clip.boundaryOut ?? "cut",
    boundaryOutCarryAudio: clip.boundaryOutCarryAudio ?? false,
    audioSource: clip.audioSource ?? "Native",
    ...(clip.uploadedAudio ? { uploadedAudio: clip.uploadedAudio } : {}),
    controlNetLora: clip.controlNetLora ?? "",
    ...(clip.icLoras ? { icLoras: clip.icLoras } : {}),
    reuseAudio: clip.reuseAudio ?? false,
    clipLengthFromAudio: clip.clipLengthFromAudio ?? false,
    stages: clip.stages.map((s) => ({
        model: s.model ?? "ltx-2.3.safetensors",
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
    mountVideoFps(24);
    mountSelect("input_videomodel", {
        value: "ltx-2.3.safetensors",
        options: ["ltx-2.3.safetensors", "ltx-2.3-alt.safetensors"],
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
        document.querySelectorAll<HTMLElement>(".vst-detail .vst-stage-tab"),
    );

const activeRailLabel = (): string | undefined =>
    document
        .querySelector<HTMLElement>(
            '.vst-detail .vst-stage-tab[aria-pressed="true"] .header-label',
        )
        ?.textContent?.replace(/^Stage /, "") ?? undefined;

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

const fieldByLabel = (label: string, scope = ".vst-detail"): HTMLElement => {
    const rows = Array.from(
        document.querySelectorAll<HTMLElement>(`${scope} .vst-detail-field`),
    );
    const row = rows.find(
        (r) =>
            r.querySelector(".vst-detail-field-label")?.firstChild
                ?.textContent === label,
    );
    if (!row) {
        throw new Error(`field not found: ${label}`);
    }
    return row;
};

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[spy.mock.calls.length - 1][0] as Clip[];

const retakeFieldByLabel = (label: string): HTMLElement =>
    fieldByLabel(label, ".vst-detail-retake-col");

/** Retakes are only authorable on a sourced clip (`retake-source-required`). */
const RETAKE_SOURCE = {
    data: "data:video/mp4;base64,AA==",
    fileName: "base.mp4",
    fps: 24,
    durationSeconds: 10,
    startSeconds: 0,
    lengthSeconds: 10,
};

describe("createTimelineDetailStrip", () => {
    let strip: TimelineDetailStrip | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        modelGlobals.modelsHelpers = {
            getDataFor: () => ({
                modelClass: { compatClass: { id: "ltxv2" } },
            }),
        };
        resetSelectionForTests();
        persistence.__resetPersistenceForTests();
        resetIcLoraAutoDownloads();
        localStorage.clear();
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
        strip = createTimelineDetailStrip();
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
                ".vst-detail .vst-detail-field-label",
            ),
        ).map((el) => el.textContent);
        expect(labels).toEqual(expect.arrayContaining(["Resolution", "FPS"]));
        // Width/Height inputs only exist while the Resolution mode is Custom.
        expect(labels).not.toContain("Dimensions");
        expect(detail()?.querySelector(".vst-settings-dims")).toBeNull();
        // The FPS field is always editable — it mirrors the core Video FPS.
        expect(
            detail()?.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="settings-fps"]',
            )?.disabled,
        ).toBe(false);
        expect(detail()?.querySelector(".vst-audio-tracks-panel")).toBeNull();
    });

    it("renders the clip/stage columns when a stage chip is clicked", () => {
        const body = setup([{ duration: 4, stages: [{}, {}] }]);
        clickRegionStageChip(body, 0, 1);
        expect(crumbText()).toBe("Clip 0 · S1");
        expect(activeRailLabel()).toBe("S1");
        expect(detailBody()?.querySelector(".vst-detail-clip")).not.toBeNull();
        expect(
            detailBody()?.querySelector(".vst-detail-repeating-group"),
        ).not.toBeNull();
        expect(
            detailBody()?.querySelector(".vst-detail-params"),
        ).not.toBeNull();
        // LoRAs are a sibling subsection to Stages, not mixed into stage
        // parameter fields.
        const loras = detailBody()?.querySelector<HTMLElement>(
            ".vst-detail-params .vst-stage-loras",
        );
        expect(loras).not.toBeNull();
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

    it("omits help popovers from the basic stage sampling fields", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        for (const text of [
            "Model",
            "Steps",
            "CFG Scale",
            "Sampler",
            "Scheduler",
        ]) {
            const label = Array.from(
                detailBody()?.querySelectorAll<HTMLElement>(
                    ".vst-detail-params .auto-input-name",
                ) ?? [],
            ).find((candidate) => candidate.textContent === text);
            expect(label).not.toBeUndefined();
            expect(
                label
                    ?.closest(".vst-detail-field, .vst-stage-slider")
                    ?.querySelector(".info-popover-button"),
            ).toBeNull();
        }
    });

    it("uses zero-based Ref labels and opens each newly added reference", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-ref")
            ?.click();
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-ref")
            ?.click();
        const groups = document.querySelectorAll<HTMLElement>(
            ".vst-detail-ref-section .vst-detail-repeating-group",
        );
        expect(groups).toHaveLength(2);
        expect(groups[0].querySelector(".header-label")?.textContent).toBe(
            "Ref0",
        );
        expect(groups[1].querySelector(".header-label")?.textContent).toBe(
            "Ref1",
        );
        expect(groups[0].classList.contains("input-group-closed")).toBe(true);
        expect(groups[1].classList.contains("input-group-open")).toBe(true);
        expect(sliderNumberByLabel("Reference R0")).not.toBeNull();
        expect(sliderNumberByLabel("Reference R1")).not.toBeNull();
    });

    it("places Count from clip end help before its label", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                refs: [{ source: "Upload", frame: 0 }],
            },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        const row = Array.from(
            detailBody()?.querySelectorAll<HTMLElement>(
                ".vst-detail-field-check",
            ) ?? [],
        ).find((candidate) =>
            candidate.textContent?.includes("Count from clip end"),
        );
        const label = row?.querySelector<HTMLElement>("label");
        expect(label?.firstElementChild?.textContent).toBe("?");
        expect(label?.lastElementChild?.textContent).toBe(
            "Count from clip end",
        );
    });

    it("places the source-video explanation at the top of its group", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const content = detailBody()?.querySelector<HTMLElement>(
            ".vst-detail-source-col",
        );
        expect(content?.firstElementChild?.textContent).toBe(
            "Use an existing video file as this clip instead of generating it.",
        );
        expect(
            content?.firstElementChild?.classList.contains(
                "vst-detail-field-hint",
            ),
        ).toBe(true);
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

    it("hides IC-LoRA strengths when the clip has no IC-LoRAs", () => {
        setup([{ duration: 4, stages: [{}], controlNetLora: "" }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(controlNetLabels()).not.toContain("IC-LoRA Strength 0");
    });

    it("shows a zero-based strength for each IC-LoRA in the stage", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "some-cnet-lora",
                        driveSource: "ControlNet 1",
                        driveData: "visual",
                    },
                    {
                        lora: "some-other-cnet-lora",
                        driveSource: "ControlNet 2",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(controlNetLabels()).toEqual(
            expect.arrayContaining([
                "IC-LoRA Strength 0",
                "IC-LoRA Strength 1",
            ]),
        );
    });

    it("persists IC-LoRA strengths independently by zero-based entry index", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    { lora: "first.safetensors", driveData: "visual" },
                    { lora: "second.safetensors", driveData: "visual" },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        jest.useFakeTimers();
        const second = sliderNumberByLabel("IC-LoRA Strength 1");
        second.value = "0.3";
        second.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].stages[0].icLoraStrengths).toEqual([
            0.8, 0.3,
        ]);
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
            lora: IC_LORA_AUTO,
            preset: "custom",
            driveSource: "Upload",
            driveData: "visual",
            driveMediaKinds: ["image", "video"],
            stage: -1,
            strength: 1,
            attentionStrength: 1,
            controlType: "none",
            driveMedia: null,
        });
        expect(document.querySelector(".vst-detail-iclora")).not.toBeNull();
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("add IC-LoRA starts at [AUTO] instead of an unrelated installed LoRA", () => {
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
        expect(clips[0].icLoras[0].lora).toBe(IC_LORA_AUTO);
        // The row survives the rebuild (the original bug: it vanished).
        expect(document.querySelectorAll(".vst-detail-iclora")).toHaveLength(1);
    });

    it("applying a preset selects its [AUTO] weights and seeds its settings", () => {
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
        expect(clips[0].icLoras[0].lora).toBe(IC_LORA_AUTO);
        expect(clips[0].icLoras[0].controlType).toBe("depth");
        expect(clips[0].icLoras[0].strength).toBe(1);
        expect(clips[0].icLoras[0].driveMediaKinds).toEqual(["image", "video"]);
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
                    ".vst-detail-iclora .vst-detail-field-label",
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

    it("gives LipDub Upload or Incoming audio without visual-guide controls", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        preset: "lipdub",
                        driveData: "audio",
                        stage: 1,
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const row = document.querySelector<HTMLElement>(".vst-detail-iclora");
        const driveMedia = Array.from(
            row?.querySelectorAll<HTMLElement>(".vst-detail-field") ?? [],
        ).find(
            (field) =>
                field.querySelector(".vst-detail-field-label")?.textContent ===
                "Drive Media",
        );
        const input =
            driveMedia?.querySelector<HTMLInputElement>('input[type="file"]');

        expect(input?.accept).toBe("audio/*,video/*");
        const labels = Array.from(
            row?.querySelectorAll(".vst-detail-field-label") ?? [],
        ).map((label) => label.textContent);
        expect(labels).not.toEqual(
            expect.arrayContaining(["Attention", "Control", "Drive data"]),
        );
        expect(labels).toContain("Source");
        const source =
            fieldByLabel("Source").querySelector<HTMLSelectElement>("select");
        expect(
            Array.from(source?.options ?? []).map((option) => option.value),
        ).toEqual(["Upload", "Incoming"]);
        expect(source?.options[1].disabled).toBe(false);
        expect(row?.textContent).toContain("Only this media's audio");
        expect(row?.textContent).toContain("frames are ignored");
    });

    it("lets Custom choose Audio and uses the same generic audio contract", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Drive data").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("Drive data select missing");
        }
        select.value = "audio";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        const entry = savedClips(saveSpy)[0].icLoras[0];
        expect(entry.driveData).toBe("audio");
        expect(entry.driveMediaKinds).toEqual(["audio", "video"]);
        expect(entry.controlType).toBe("none");
        expect(controlNetLabels()).not.toEqual(
            expect.arrayContaining(["Attention", "Control"]),
        );
        expect(
            fieldByLabel("Drive Media").querySelector<HTMLInputElement>(
                'input[type="file"]',
            )?.accept,
        ).toBe("audio/*,video/*");
    });

    it("lets Custom choose a model-only patch and clears hidden drive media", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        driveData: "visual",
                        driveMedia: {
                            data: "data:image/png;base64,AA==",
                            fileName: "guide.png",
                        },
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Drive data").querySelector<HTMLSelectElement>(
                "select",
            );
        if (!select) {
            throw new Error("Drive data select missing");
        }
        select.value = "none";
        select.dispatchEvent(new Event("change", { bubbles: true }));

        expect(savedClips(saveSpy)[0].icLoras[0]).toMatchObject({
            driveData: "none",
            driveSource: "Upload",
            driveMedia: null,
        });
        expect(controlNetLabels()).not.toEqual(
            expect.arrayContaining(["Source", "Drive Media"]),
        );
    });

    it("uses persisted image-only Drive Media kinds for Upload and Incoming gating", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        driveData: "visual",
                        driveMediaKinds: ["image"],
                        stage: 1,
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        expect(
            fieldByLabel("Drive Media").querySelector<HTMLInputElement>(
                'input[type="file"]',
            )?.accept,
        ).toBe("image/*");
        expect(
            fieldByLabel("Source").querySelector<HTMLSelectElement>("select")
                ?.options[1].disabled,
        ).toBe(true);
    });

    // The per-entry selects render in a fixed order: Preset, LoRA, Control
    // (Custom / Union Control presets only), Apply on, then Source
    // (refine-stage placements only).
    const IC_LORA_SELECTS = [
        "preset",
        "lora",
        "control",
        "apply",
        "data",
        "source",
    ];
    type IcLoraSelectName = "preset" | "lora" | "apply" | "data" | "source";
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
        // Custom exposes Drive data and Source; Incoming is disabled because
        // the all-stages target includes a generated stage 0.
        expect(
            document.querySelectorAll(".vst-detail-iclora select"),
        ).toHaveLength(6);
        expect(icLoraSelect("source").options[1].disabled).toBe(true);
    });

    it("refine-stage placement offers Incoming and swaps the upload row for a hint", () => {
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

        changeIcLoraSelect("source", "Incoming");
        const entry = savedClips(saveSpy)[0].icLoras[0];
        expect(entry.driveSource).toBe("Incoming");
        expect(controlNetLabels()).not.toContain("Drive Media");
        expect(detail()?.textContent).toContain(
            "Uses visual from stage 1's incoming media.",
        );
    });

    it("moving an Incoming entry to an unavailable scope resets it to Upload", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        changeIcLoraSelect("apply", "-1");
        const entry = savedClips(saveSpy)[0].icLoras[0];
        expect(entry.stage).toBe(-1);
        expect(entry.driveSource).toBe("Upload");
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
        // Custom also exposes Drive data, and Incoming is available because
        // source footage enters stage 0.
        expect(
            document.querySelectorAll(".vst-detail-iclora select"),
        ).toHaveLength(6);
        expect(icLoraSelect("source").value).toBe("Upload");
        expect(controlNetLabels()).toContain("Drive Media");
        expect(icLoraSelect("source").options[1].disabled).toBe(false);
    });

    it("sourced clip Incoming entry shows its data source at stage 0", () => {
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
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(icLoraSelect("source").value).toBe("Incoming");
        expect(controlNetLabels()).not.toContain("Drive Media");
        expect(detail()?.textContent).toContain(
            "Uses visual from stage 0's incoming media.",
        );
    });

    it("unsourced clip disables Incoming on a stage-0/all entry", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(
            document.querySelectorAll(".vst-detail-iclora select"),
        ).toHaveLength(6);
        expect(icLoraSelect("source").options[1].disabled).toBe(true);
        expect(controlNetLabels()).toContain("Drive Media");
    });

    it("does not mistake a skipped authored stage for prior-stage Incoming media", () => {
        setup([
            {
                duration: 4,
                stages: [{ skipped: true }, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        expect(icLoraSelect("source").options[1].disabled).toBe(true);
    });

    it("repairs Incoming to Upload when skipping its supplying stage", () => {
        setup([
            {
                duration: 4,
                stages: [{}, {}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 1,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
            )
            ?.click();

        expect(savedClips(saveSpy)[0].icLoras[0].driveSource).toBe("Upload");
        expect(
            fieldByLabel("Drive Media").querySelector<HTMLInputElement>(
                'input[type="file"]',
            )?.accept,
        ).toBe("image/*,video/*");
    });

    it("repairs a later clip's Incoming source when its prior clip is skipped", () => {
        setup([
            { duration: 4, stages: [{}] },
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-skip-clip")
            ?.click();

        expect(savedClips(saveSpy)[1].icLoras[0].driveSource).toBe("Upload");
    });

    it("uses the nearest executable earlier clip for Incoming availability", () => {
        setup([
            { duration: 4, stages: [{}] },
            { duration: 4, skipped: true, stages: [{}] },
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 2, stageIdx: 0 });
        expect(icLoraSelect("source").options[1].disabled).toBe(false);
    });

    it("does not treat a skipped earlier clip as Incoming output", () => {
        setup([
            { duration: 4, skipped: true, stages: [{}] },
            {
                duration: 4,
                stages: [{}],
                icLoras: [
                    {
                        lora: "lora-x.safetensors",
                        stage: 0,
                        driveData: "visual",
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(icLoraSelect("source").options[1].disabled).toBe(true);
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

    it("removes an IC-LoRA entry via the rail Delete button", () => {
        setup([
            {
                duration: 4,
                stages: [{}],
                icLoras: [{ lora: "lora-x.safetensors" }],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const removeBtn = document.querySelector<HTMLButtonElement>(
            ".vst-detail-delete-iclora",
        );
        if (!removeBtn) {
            throw new Error("remove IC-LoRA button missing");
        }
        removeBtn.click();
        expect(savedClips(saveSpy)[0].icLoras).toHaveLength(0);
    });

    it("live-applies a discrete model command through the document store", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const select =
            fieldByLabel("Model").querySelector<HTMLSelectElement>("select");
        if (!select) {
            throw new Error("model select missing");
        }
        select.value = "ltx-2.3-alt.safetensors";
        select.dispatchEvent(new Event("change", { bubbles: true }));
        expect(persistence.getClips()[0].stages[0].model).toBe(
            "ltx-2.3-alt.safetensors",
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
        select.value = "ltx-2.3-alt.safetensors";
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

    it("remaps IC-LoRA stage targets when a stage is deleted", () => {
        const body = setup([
            {
                duration: 4,
                stages: [{}, {}, {}],
                icLoras: [
                    { lora: "a", stage: 2 },
                    {
                        lora: "b",
                        stage: 1,
                        driveSource: "Incoming",
                        driveData: "visual",
                    },
                    { lora: "c", stage: 0 },
                    { lora: "d", stage: -1 },
                ],
            },
        ]);
        clickRegionStageChip(body, 0, 1, true);
        const clip = savedClips(saveSpy)[0];
        expect(clip.stages).toHaveLength(2);
        // Above the deleted stage shifts down; on it falls back to all stages.
        // Removing the supplying stage also repairs stale Incoming state.
        expect(clip.icLoras.map((e) => e.stage)).toEqual([1, -1, 0, -1]);
        expect(clip.icLoras[1].driveSource).toBe("Upload");
    });

    it("adds a stage from the rail's Add button and selects it", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        // Delete is present but DISABLED with a single stage (never hidden).
        const del = document.querySelector<HTMLButtonElement>(
            ".vst-detail-delete-stage",
        );
        expect(del).not.toBeNull();
        expect(del?.textContent).toBe("×");
        expect(del?.disabled).toBe(true);
        const add = document.querySelector<HTMLElement>(
            ".vst-detail-add-stage",
        );
        expect(add?.textContent).toBe("+ Add Video Stage");
        add?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(2);
        expect(activeRailLabel()).toBe("S1");
        expect(crumbText()).toBe("Clip 0 · S1");
        expect(
            document.querySelector<HTMLButtonElement>(
                ".vst-detail-delete-stage",
            )?.disabled,
        ).toBe(false);
    });

    it("adds the first architecture stage to a zero-stage source-only clip", () => {
        setup([
            {
                duration: 4,
                sourceVideo: {
                    data: "data:video/mp4;base64,AA==",
                    fileName: "source.mp4",
                    fps: 24,
                    durationSeconds: 4,
                    startSeconds: 0,
                    lengthSeconds: 4,
                },
                stages: [],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        const add = document.querySelector<HTMLButtonElement>(
            ".vst-detail-add-stage",
        );

        expect(add?.disabled).toBe(false);
        add?.click();

        expect(persistence.getClips()[0].stages).toHaveLength(1);
        expect(persistence.getClips()[0].architecture).not.toBe("none");
        expect(activeRailLabel()).toBe("S0");
    });

    it("mutes the stage params and persists Skip this stage", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const fields =
            document.querySelector<HTMLElement>(".vst-detail-fields");
        expect(fields?.classList.contains("vst-stage-fields-muted")).toBe(
            false,
        );
        document
            .querySelector<HTMLButtonElement>(
                '.vst-stage-tab[aria-pressed="true"] .vst-detail-skip-stage',
            )
            ?.click();
        expect(
            document
                .querySelector<HTMLElement>(".vst-detail-fields")
                ?.classList.contains("vst-stage-fields-muted"),
        ).toBe(true);
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
        setup([{ duration: 4, stages: [{}], sourceVideo: RETAKE_SOURCE }]);
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

    it("shows no second Retake add action once a retake exists", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                sourceVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(
            document.querySelector<HTMLButtonElement>(".vst-detail-add-retake"),
        ).toBeNull();
    });

    it("renders the retake editor with the breadcrumb, fields, note and remove", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 0.6 },
                sourceVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(crumbText()).toBe("Retake · Clip 0 · 2–5 s");
        expect(
            retakeFieldByLabel("Start (s)").querySelector<HTMLInputElement>(
                "input",
            )?.value,
        ).toBe("2");
        expect(
            retakeFieldByLabel("Length (s)").querySelector<HTMLInputElement>(
                "input",
            )?.value,
        ).toBe("3");
        expect(sliderNumberByLabel("Strength").value).toBe("0.6");
        expect(
            detail()?.querySelector(".vst-detail-retake-col .vst-detail-note")
                ?.textContent,
        ).toContain("Applies when refining a base video");
        expect(
            detailBody()?.querySelector(".vst-detail-delete")?.textContent,
        ).toBe("×");
    });

    it("live-applies a retake Start edit through the debounce", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                sourceVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        jest.useFakeTimers();
        const start =
            retakeFieldByLabel("Start (s)").querySelector<HTMLInputElement>(
                "input",
            );
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
                sourceVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        // The clip panel (bare fields + Stages group) is what renders…
        expect(detailBody()?.querySelector(".vst-detail-clip")).not.toBeNull();
        expect(
            detail()?.querySelector('[data-vst-repeater-key="stages"]'),
        ).not.toBeNull();
        // …and its Retake section carries the editable fields.
        expect(
            detail()?.querySelector('[data-vst-accordion-key="retake"]'),
        ).not.toBeNull();
        expect(
            detail()?.querySelector('[data-vst-repeater-key="retakes"]'),
        ).toBeNull();
        expect(
            detail()
                ?.querySelector(".vst-detail-retake-section")
                ?.querySelector(".vst-detail-repeating-group"),
        ).toBeNull();
        expect(
            detailBody()?.querySelector(
                'input[data-vst-focus-key="retake-start"]',
            ),
        ).not.toBeNull();
        expect(crumbText()).toBe("Retake · Clip 0 · 2–5 s");
    });

    it("removes the retake without leaving or collapsing its section", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                retake: { startSeconds: 2, lengthSeconds: 3, strength: 1 },
                sourceVideo: RETAKE_SOURCE,
            },
        ]);
        setSelection({ kind: "retake", clipIdx: 0 });
        const beforeDelete = detailBody();
        if (!beforeDelete) {
            throw new Error("dock body missing");
        }
        beforeDelete.scrollTop = 140;
        beforeDelete
            ?.querySelector<HTMLElement>(".vst-detail-delete-retake")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].retake).toBeNull();
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        expect(
            detail()
                ?.querySelector('[data-vst-accordion-key="retake"]')
                ?.classList.contains("input-group-open"),
        ).toBe(true);
        expect(
            detail()?.querySelector(".vst-detail-add-retake"),
        ).not.toBeNull();
        expect(detailBody()?.scrollTop).toBe(140);
    });

    it("keeps the empty single-instance Retake section selectable", () => {
        setup([{ duration: 4, stages: [{}], sourceVideo: RETAKE_SOURCE }]);
        setSelection({ kind: "retake", clipIdx: 0 });
        expect(crumbText()).toBe("Retake · Clip 0");
        expect(getSelection()).toEqual({ kind: "retake", clipIdx: 0 });
        expect(
            detail()
                ?.querySelector('[data-vst-accordion-key="retake"]')
                ?.classList.contains("input-group-open"),
        ).toBe(true);
    });

    it("adds and persists a LoRA row", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLElement>(
                ".vst-detail .vst-stage-loras > .input-group-header",
            )
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        document
            .querySelector<HTMLElement>(".vst-detail .vst-stage-lora-add")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(
            document.querySelectorAll(".vst-detail .vst-stage-lora-entry"),
        ).toHaveLength(1);
        expect(saveSpy).toHaveBeenCalled();
        expect(savedClips(saveSpy)[0].stages[0].loras).toEqual([
            { name: "lora-x.safetensors", weight: 1 },
        ]);
        expect(
            document
                .querySelector(".vst-detail .vst-stage-loras")
                ?.classList.contains("input-group-open"),
        ).toBe(true);
    });

    it("uses zero-based LoRA labels and opens the newly added LoRA", () => {
        setup([
            {
                duration: 4,
                stages: [
                    { loras: [{ name: "lora-x.safetensors", weight: 0.5 }] },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-stage-lora-add")
            ?.click();
        const groups = document.querySelectorAll<HTMLElement>(
            ".vst-stage-lora-entry",
        );
        expect(groups).toHaveLength(2);
        expect(groups[0].querySelector(".header-label")?.textContent).toBe(
            "LoRA 0",
        );
        expect(groups[1].querySelector(".header-label")?.textContent).toBe(
            "LoRA 1",
        );
        expect(groups[0].classList.contains("input-group-closed")).toBe(true);
        expect(groups[1].classList.contains("input-group-open")).toBe(true);
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
            ".vst-detail .vst-stage-lora-entry",
        );
        expect(row).not.toBeNull();
        const nameSelect = row?.querySelector<HTMLSelectElement>("select");
        expect(nameSelect?.value).toBe("lora-x.safetensors");
        // Name renders at input font size (via .vst-audio-select), not the
        // small 3xs label size.
        expect(nameSelect?.classList.contains("vst-audio-select")).toBe(true);
        const weight =
            row?.querySelector<HTMLInputElement>("input.auto-number");
        expect(weight?.value).toBe("0.7");
        expect(weight?.step).toBe("0.05");
        expect(weight?.hasAttribute("min")).toBe(false);
        expect(weight?.hasAttribute("max")).toBe(false);
        expect(row?.querySelector("input.auto-slider-range")).toBeNull();
        expect(fieldByLabel("Weight").classList).toContain("auto-number-box");
        expect(
            fieldByLabel("Weight").querySelector(".info-popover-button"),
        ).toBeNull();
        expect(
            detailBody()?.querySelector(".vst-stage-lora-remove"),
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
            '.vst-detail input[data-vst-focus-key="stage-0-lora-0-weight"]',
        );
        if (!weight) {
            throw new Error("lora weight input missing");
        }
        expect(weight.getAttribute("data-vst-focus-key")).toBe(
            "stage-0-lora-0-weight",
        );
        weight.value = "0.4";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        expect(saveSpy).not.toHaveBeenCalled();
        jest.advanceTimersByTime(200);
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages[0].loras[0].weight).toBe(0.4);
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
            '.vst-detail input[data-vst-focus-key="stage-0-lora-0-weight"]',
        );
        if (!weight) throw new Error("lora weight input missing");
        weight.value = "-2.5";
        weight.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);
        expect(savedClips(saveSpy)[0].stages[0].loras[0].weight).toBe(-2.5);
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
            document.querySelectorAll(".vst-detail .vst-stage-lora-entry"),
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
            document.querySelectorAll(".vst-detail .vst-stage-lora-entry"),
        ).toHaveLength(1);
        expect(
            document
                .querySelector(".vst-detail .vst-stage-loras")
                ?.classList.contains("input-group-open"),
        ).toBe(true);
    });

    it("uses SwarmUI's native full-width flex class for every stage slider", () => {
        setup([
            {
                duration: 4,
                refs: [{ source: "Upload", frame: 0 }],
                stages: [
                    {
                        loras: [{ name: "lora-x.safetensors", weight: 0.7 }],
                    },
                ],
            },
        ]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        for (const label of ["Steps", "CFG Scale", "Reference R0"]) {
            expect(
                sliderNumberByLabel(label)
                    .closest(".vst-stage-slider")
                    ?.classList.contains("auto-input-flex-wide"),
            ).toBe(true);
        }
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
        const stages = savedClips(saveSpy)[0].stages;
        expect(stages).toHaveLength(2);
        expect(stages[1].loras).toEqual([
            { name: "lora-x.safetensors", weight: 0.65 },
        ]);
    });

    it("deletes the current stage from the rail's Delete stage button", () => {
        setup([{ duration: 4, stages: [{}, {}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
        const deleteBtn = document.querySelector<HTMLElement>(
            '.vst-stage-tab[aria-pressed="true"] .vst-detail-delete-stage',
        );
        expect(deleteBtn).not.toBeNull();
        deleteBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].stages).toHaveLength(1);
    });

    it("replaces Clear/collapse with a gear modal and persists its toggles", () => {
        setup([{ duration: 4, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        expect(detail()?.querySelector(".vst-detail-clear")).toBeNull();
        expect(detail()?.querySelector(".vst-detail-collapse")).toBeNull();
        const gear = detail()?.querySelector<HTMLButtonElement>(
            ".vst-detail-settings-button",
        );
        expect(gear?.getAttribute("aria-label")).toBe("Timeline settings");
        gear?.click();

        const modal = document.querySelector<HTMLElement>(
            ".vst-timeline-settings-modal",
        );
        expect(modal?.getAttribute("role")).toBe("dialog");
        const checks = modal?.querySelectorAll<HTMLInputElement>(
            'input[type="checkbox"]',
        );
        expect(checks).toHaveLength(2);
        expect(checks?.[0].checked).toBe(true);
        expect(checks?.[1].checked).toBe(true);
        if (checks?.[0]) {
            checks[0].checked = false;
            checks[0].dispatchEvent(new Event("change", { bubbles: true }));
        }
        expect(
            JSON.parse(
                localStorage.getItem(
                    "videostages.timeline.authoringSettings",
                ) ?? "{}",
            ),
        ).toMatchObject({ snap: false, autoCollapse: true });
    });

    it("clamps the selection to none after its clip is removed", () => {
        const body = setup([
            { duration: 4, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        setSelection({ kind: "clip", clipIdx: 1, stageIdx: 0 });
        expect(crumbText()).toBe("Clip 1 · S0");

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

    it("adds and selects a reference from the clip sidebar rail", () => {
        setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-ref")
            ?.click();
        expect(savedClips(saveSpy)[0].refs).toHaveLength(1);
        expect(getSelection()).toEqual({
            kind: "ref",
            clipIdx: 0,
            refIdx: 0,
        });
        expect(refRow(0)).not.toBeNull();
    });

    it("lists every ref in a rail and edits only the selected one", () => {
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
        expect(crumbText()).toBe("Ref1 · Clip 0");
        expect(document.querySelectorAll(".vst-detail-ref-row")).toHaveLength(
            1,
        );
        expect(document.querySelectorAll(".vst-ref-tab")).toHaveLength(2);
        expect(
            refRow(1).classList.contains("vst-detail-repeating-editor-active"),
        ).toBe(true);

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

        // Rail delete removes the active ref, selects its neighbour, and
        // preserves the dock's scroll position.
        const beforeDelete = detailBody();
        if (!beforeDelete) {
            throw new Error("dock body missing");
        }
        beforeDelete.scrollTop = 140;
        document
            .querySelector<HTMLElement>(".vst-detail-delete-ref")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].refs).toHaveLength(1);
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
        expect(detailBody()?.scrollTop).toBe(140);
        expect(refRow(0).dataset.vstRefIndex).toBe("0");
    });

    it("reveals References when an already-selected timeline ref is activated", () => {
        const original = Object.getOwnPropertyDescriptor(
            HTMLElement.prototype,
            "scrollIntoView",
        );
        const reveal = jest.fn();
        Object.defineProperty(HTMLElement.prototype, "scrollIntoView", {
            configurable: true,
            value: reveal,
        });
        try {
            setup([
                {
                    duration: 5,
                    stages: [{}],
                    refs: [{ source: "Base", frame: 1 }],
                },
            ]);
            setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
            reveal.mockClear();
            activateSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
            expect(getSelection()).toEqual({
                kind: "ref",
                clipIdx: 0,
                refIdx: 0,
            });
            expect(reveal).toHaveBeenCalledTimes(1);
            expect(
                document.querySelector('[data-vst-repeater-key="references"]'),
            ).not.toBeNull();
        } finally {
            if (original) {
                Object.defineProperty(
                    HTMLElement.prototype,
                    "scrollIntoView",
                    original,
                );
            } else {
                Reflect.deleteProperty(HTMLElement.prototype, "scrollIntoView");
            }
        }
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
        document
            .querySelector<HTMLElement>(".vst-detail-delete-ref")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        expect(savedClips(saveSpy)[0].refs).toHaveLength(0);
        expect(getSelection()).toEqual({
            kind: "clip",
            clipIdx: 0,
            stageIdx: 0,
        });
    });

    it("switches the active reference editor from the reference rail", () => {
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
        const before = refRow(0);
        document
            .querySelectorAll<HTMLButtonElement>(".vst-ref-tab")[1]
            ?.click();
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });
        expect(refRow(1)).not.toBe(before);
        expect(
            refRow(1).classList.contains("vst-detail-repeating-editor-active"),
        ).toBe(true);
    });

    it("clamps an edited ref frame and writes it through saveClips", () => {
        setup([
            { duration: 5, stages: [{}], refs: [{ source: "Base", frame: 1 }] },
        ]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        jest.useFakeTimers();
        const frameRow = Array.from(
            document.querySelectorAll<HTMLElement>(
                ".vst-detail .vst-detail-field",
            ),
        ).find((r) =>
            r
                .querySelector(".vst-detail-field-label")
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
                icLoras: [
                    {
                        lora: "some-lora",
                        driveSource: "ControlNet 1",
                        driveData: "visual",
                    },
                ],
            },
        ]);
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
            ".vst-audio-track-add",
        );
        expect(addBtn).not.toBeNull();
        expect(addBtn?.textContent).toBe("+ Add Audio Segment");
        addBtn?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection()).toEqual({
            kind: "audio-track",
            trackIdx: 0,
        });
        const segments = persistence.getState().audioTracks ?? [];
        expect(segments).toHaveLength(1);
        expect(segments[0].spans[0].timelineStartSeconds).toBe(0);
        expect(segments[0].spans[0].timelineLengthSeconds).toBe(2);
        expect(segments[0].volume).toBe(1);
        expect(segments[0].source.uploadedAudio).toBeNull();
    });

    it("filters timeline-wide segments to the selected clip audio window", () => {
        setup([
            { duration: 3, stages: [{}] },
            { duration: 4, stages: [{}] },
        ]);
        const state = persistence.getState();
        state.audioTracks = [
            {
                id: "track-clip-0",
                volume: 1,
                source: {
                    kind: "Upload",
                    reference: "",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-clip-0",
                        timelineStartSeconds: 0,
                        timelineLengthSeconds: 1,
                        sourceStartSeconds: 0,
                    },
                ],
            },
            {
                id: "track-both",
                volume: 1,
                source: {
                    kind: "Upload",
                    reference: "",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-both",
                        timelineStartSeconds: 2,
                        timelineLengthSeconds: 2,
                        sourceStartSeconds: 0,
                    },
                ],
            },
            {
                id: "track-clip-1",
                volume: 1,
                source: {
                    kind: "Upload",
                    reference: "",
                    uploadedAudio: null,
                },
                spans: [
                    {
                        id: "span-clip-1",
                        timelineStartSeconds: 3,
                        timelineLengthSeconds: 1,
                        sourceStartSeconds: 0,
                    },
                ],
            },
        ];
        persistence.saveState(state, { notifyDomChange: false });
        const visibleSegmentLabels = (): string[] =>
            Array.from(
                document.querySelectorAll<HTMLElement>(
                    ".vst-audio-track-tab .header-label",
                ),
            ).map((label) => label.textContent ?? "");

        setSelection({ kind: "audio", clipIdx: 0 });
        expect(visibleSegmentLabels()).toEqual(["S0", "S1"]);

        setSelection({ kind: "audio", clipIdx: 1 });
        expect(visibleSegmentLabels()).toEqual(["S1", "S2"]);
    });

    it("edits the clip's major prompt (debounced) through saveClips", () => {
        setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        expect(crumbText()).toBe("Prompts · Clip 0");
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

    it("adds and selects a relay prompt from the combined prompt sidebar", () => {
        setup([{ duration: 5, stages: [{}] }]);
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        document
            .querySelector<HTMLButtonElement>(".vst-detail-add-relay")
            ?.click();
        expect(savedClips(saveSpy)[0].promptWindows).toHaveLength(1);
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });
        expect(minorEditor(0)).not.toBeNull();
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

    it("lists every relay in a rail and renders only the selected editor", () => {
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
        expect(rows).toHaveLength(1);
        expect(rows[0].dataset.vstMinorWindow).toBe("1");
        expect(document.querySelectorAll(".vst-relay-tab")).toHaveLength(3);
        const beginEnd = (row: HTMLElement): [string, string] => [
            row.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="-begin"]',
            )?.value ?? "",
            row.querySelector<HTMLInputElement>(
                'input[data-vst-focus-key$="-end"]',
            )?.value ?? "",
        ];
        expect(beginEnd(rows[0])).toEqual(["4", "6"]);
        expect(rows[0].querySelector("textarea")).not.toBeNull();
        expect(
            document
                .querySelectorAll(".vst-relay-tab")[1]
                .getAttribute("aria-pressed"),
        ).toBe("true");
        expect(document.activeElement).toBe(minorEditor(1));
    });

    it("switches the active relay editor from the relay rail", () => {
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

        const before = minorEditor(0);
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 1,
        });
        expect(minorEditor(1)).not.toBe(before);
        expect(document.activeElement).toBe(minorEditor(1));
        expect(minorRows()[0].dataset.vstMinorWindow).toBe("1");
    });

    it("flushes one relay edit before switching to another relay", () => {
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

        const e0 = minorEditor(0);
        e0.value = "red car";
        e0.dispatchEvent(new Event("input", { bubbles: true }));
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
        expect(savedClips(saveSpy)[0].promptWindows[0].prompt).toBe("red car");
        const e1 = minorEditor(1);
        e1.value = "blue sky";
        e1.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(200);

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

    it("deletes the active relay window via the rail Delete button", () => {
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
        document
            .querySelector<HTMLElement>(
                '.vst-relay-tab[aria-pressed="true"] .vst-detail-delete-relay',
            )
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
        // Switch to w1: its BEGIN spinner stops at w0's end (3).
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
        const w1Begin = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-1-begin"]',
        );
        expect(w1Begin?.min).toBe("3");
        // Outer edges stay clip-bounded.
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[0]
            ?.click();
        const w0Begin = minorRows()[0].querySelector<HTMLInputElement>(
            'input[data-vst-focus-key="minor-0-begin"]',
        );
        expect(w0Begin?.min).toBe("0");
        document
            .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
            ?.click();
        const w1End = minorRows()[0].querySelector<HTMLInputElement>(
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
            expect(crumbText()).toBe("Relay 1–4s · Clip 0");
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
            expect(refRow(0).querySelector(".vst-audio-upload")).toBeNull();
            const select = refRow(0).querySelector<HTMLSelectElement>("select");
            if (!select) {
                throw new Error("ref source select missing");
            }
            select.value = "Upload";
            select.dispatchEvent(new Event("change", { bubbles: true }));
            // Structure changed: the upload row appeared and the panel rebuilt
            // (the select is a fresh node).
            expect(refRow(0).querySelector(".vst-audio-upload")).not.toBeNull();
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
            const begin = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-1-begin"]',
            );
            if (!begin) {
                throw new Error("begin input missing");
            }
            commitNumber(begin, "1");
            // Stored begin clamped to the neighbour bound (W0 ends at 3); the
            // input is corrected to the stored value, not the typed 1.
            expect(savedClips(saveSpy)[0].promptWindows[1].start).toBe(3);
            const after = minorRows()[0].querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="minor-1-begin"]',
            );
            expect(after).toBe(begin); // same node — no rebuild
            expect(after?.value).toBe("3"); // shows the clamped stored value
            expect(document.activeElement).toBe(begin); // focus intact
            expect(crumbText()).toBe("Relay 3–8s · Clip 0");
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
                    sourceVideo: RETAKE_SOURCE,
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "retake", clipIdx: 0 });
            const len = retakeFieldByLabel(
                "Length (s)",
            ).querySelector<HTMLInputElement>(
                'input[data-vst-focus-key="retake-length"]',
            );
            if (!len) {
                throw new Error("retake length input missing");
            }
            commitNumber(len, "5");
            expect(savedClips(saveSpy)[0].retake?.lengthSeconds).toBe(2);
            const after = retakeFieldByLabel(
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
                    sourceVideo: RETAKE_SOURCE,
                },
            ]);
            wireLiveRenders();
            setSelection({ kind: "retake", clipIdx: 0 });
            jest.useFakeTimers();
            const len = retakeFieldByLabel(
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

    it("renders major and relay prompts in one sidebar", () => {
        setup([
            {
                duration: 10,
                stages: [{}],
                windows: [{ start: 2, duration: 3, prompt: "x" }],
            },
        ]);

        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const major = detail()?.querySelector<HTMLElement>(
            ".vst-detail-prompt-major",
        );
        const relayHead = detail()?.querySelector<HTMLElement>(
            '[data-vst-repeater-key="relay-prompts"]',
        );
        expect(major).not.toBeNull();
        expect(relayHead).not.toBeNull();
        expect(relayHead?.classList.contains("vst-detail-relay-section")).toBe(
            true,
        );
        expect(
            detailBody()?.querySelector(
                ".vst-detail-repeating-group .input-group-header",
            ),
        ).not.toBeNull();
        expect(
            relayHead?.querySelector(
                ':scope > .input-group-content > [data-vst-repeater-item="0"] .header-label',
            )?.textContent,
        ).toBe("R0");
        expect(crumbText()).toBe("Prompts · Clip 0");
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
            const range = rangeByLabel("Reference R0");
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
            expect(rangeByLabel("Reference R0")).toBe(range);
            jest.useRealTimers();
        });

        it("commits exactly once on pointer release", () => {
            setup([refClip()]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            jest.useFakeTimers();
            const range = rangeByLabel("Reference R0");
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
            const range = rangeByLabel("Reference R0");
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
        it("keeps Clip fields visible above progressive native accordion sections", () => {
            setup([
                {
                    duration: 4,
                    stages: [{}, {}, {}],
                    sourceVideo: RETAKE_SOURCE,
                },
            ]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });

            const clipCol = detailBody()?.querySelector(".vst-detail-clip");
            expect(clipCol).not.toBeNull();
            expect(
                clipCol?.closest('[data-vst-accordion-key="clip"]'),
            ).toBeNull();
            expect(clipCol?.classList.contains("input-group-content")).toBe(
                true,
            );
            expect(
                clipCol
                    ?.closest(".vst-detail-clip-section")
                    ?.querySelector(".vst-detail-skip-clip"),
            ).not.toBeNull();
            const clipSection = clipCol?.closest<HTMLElement>(
                '[data-vst-static-key="clip"]',
            );
            expect(clipSection?.classList.contains("input-group")).toBe(true);
            expect(clipSection?.classList.contains("input-group-open")).toBe(
                true,
            );
            expect(
                clipSection?.querySelector(
                    ":scope > .input-group-header.input-group-shrinkable",
                ),
            ).toBeNull();

            const stagesSection = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"]',
            );
            expect(stagesSection).not.toBeNull();
            expect(stagesSection?.classList.contains("input-group")).toBe(true);
            expect(stagesSection?.classList.contains("input-group-open")).toBe(
                true,
            );
            expect(
                stagesSection?.querySelector(
                    ":scope > .input-group-header .header-label",
                )?.textContent,
            ).toBe("Stages");
            expect(
                stagesSection?.querySelectorAll(
                    ":scope > .input-group-content > .vst-detail-repeating-group",
                ),
            ).toHaveLength(3);
            expect(
                stagesSection?.querySelector(
                    ".vst-detail-repeating-group .vst-detail-params",
                ),
            ).not.toBeNull();
            expect(
                detailBody()?.querySelector(
                    ".vst-detail-params .vst-stage-loras",
                ),
            ).not.toBeNull();

            const retakeSec = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="retake"]',
            );
            expect(retakeSec).not.toBeNull();
            expect(
                retakeSec?.querySelector(
                    ":scope > .input-group-header .header-label",
                )?.lastChild?.textContent,
            ).toBe("Retake");
            expect(
                retakeSec?.querySelector(".info-popover-button"),
            ).not.toBeNull();
            expect(
                detailBody()?.querySelector(".vst-detail-add-retake"),
            ).not.toBeNull();
        });

        it("lists references above IC-LoRAs using the shared selector rails", () => {
            setup([
                {
                    duration: 10,
                    stages: [{}],
                    refs: [
                        { source: "Base", frame: 1 },
                        { source: "Refiner", frame: 8 },
                    ],
                    icLoras: [{ lora: "a" }, { lora: "b" }],
                },
            ]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const body = detailBody();
            const refsHead = body?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="references"]',
            );
            const icLorasHead = body?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="ic-loras"]',
            );
            expect(refsHead).not.toBeNull();
            expect(icLorasHead).not.toBeNull();
            expect(
                refsHead && icLorasHead
                    ? refsHead.compareDocumentPosition(icLorasHead) &
                          Node.DOCUMENT_POSITION_FOLLOWING
                    : 0,
            ).toBeTruthy();
            expect(body?.querySelectorAll(".vst-ref-tab")).toHaveLength(2);
            expect(
                body?.querySelector(".vst-detail-add-ref")?.textContent,
            ).toBe("+ Add Reference Image");
            expect(
                body?.querySelector(".vst-detail-delete-ref")?.textContent,
            ).toBe("×");
            expect(body?.querySelectorAll(".vst-iclora-tab")).toHaveLength(2);
            expect(body?.querySelectorAll(".vst-detail-iclora")).toHaveLength(
                1,
            );
            expect(
                body?.querySelector(".vst-detail-add-iclora")?.textContent,
            ).toBe("+ Add IC-LoRA");
            expect(
                body?.querySelector(".vst-detail-delete-iclora")?.textContent,
            ).toBe("×");

            const itemStructure = (
                section: Element | null | undefined,
            ): string[][] =>
                Array.from(
                    section?.querySelectorAll(
                        ":scope > .input-group-content > .vst-detail-repeating-group",
                    ) ?? [],
                ).map((item) =>
                    Array.from(item.children).map((child) =>
                        child.classList.contains("input-group-header")
                            ? "header"
                            : child.classList.contains("input-group-content")
                              ? "content"
                              : "other",
                    ),
                );
            expect(itemStructure(refsHead)).toEqual([
                ["header", "content"],
                ["header", "content"],
            ]);
            expect(itemStructure(icLorasHead)).toEqual(itemStructure(refsHead));

            setSelection({ kind: "ic-lora", clipIdx: 0, entryIdx: 1 });
            expect(
                detailBody()
                    ?.querySelector(".vst-detail-iclora")
                    ?.getAttribute("data-vst-iclora-idx"),
            ).toBe("1");
            const firstIcHeader = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="ic-loras"] [data-vst-repeater-item="0"] > .input-group-header',
            );
            firstIcHeader?.click();
            expect(
                detailBody()
                    ?.querySelector(".vst-detail-iclora")
                    ?.getAttribute("data-vst-iclora-idx"),
            ).toBe("0");
            expect(
                detailBody()
                    ?.querySelector(
                        '[data-vst-repeater-key="ic-loras"] [data-vst-repeater-item="0"]',
                    )
                    ?.classList.contains("input-group-open"),
            ).toBe(true);
        });

        it("uses local native groups without consulting host group cookies", () => {
            const globals = globalThis as unknown as {
                getCookie: (name: string) => string;
            };
            expect(typeof globals.getCookie).toBe("function");
            jest.spyOn(globals, "getCookie").mockImplementation(() => "closed");
            setup([{ duration: 4, stages: [{}] }]);
            const body = detailBody();
            expect(body?.querySelector(".vst-detail-settings")).not.toBeNull();
            const settings = body?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="timeline-settings"]',
            );
            expect(settings?.classList.contains("input-group-open")).toBe(true);
            expect(globals.getCookie).not.toHaveBeenCalled();
        });

        it("keeps only one top-level section open at a time", () => {
            setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const hostDelegatedToggle = jest.fn();
            document.addEventListener("click", hostDelegatedToggle);
            const stages = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"]',
            );
            const source = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="source-video"]',
            );
            expect(stages?.classList.contains("input-group-open")).toBe(true);
            source
                ?.querySelector<HTMLElement>(":scope > .input-group-header")
                ?.click();
            expect(source?.classList.contains("input-group-open")).toBe(true);
            expect(stages?.classList.contains("input-group-closed")).toBe(true);
            expect(
                source?.querySelector<HTMLElement>(
                    ":scope > .input-group-content",
                )?.hidden,
            ).toBe(false);
            expect(
                source
                    ?.querySelector<HTMLElement>(":scope > .input-group-header")
                    ?.getAttribute("aria-expanded"),
            ).toBe("true");
            expect(hostDelegatedToggle).not.toHaveBeenCalled();
            document.removeEventListener("click", hostDelegatedToggle);
        });

        it("keeps other sections open when Auto-collapse is disabled", () => {
            setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            detail()
                ?.querySelector<HTMLButtonElement>(
                    ".vst-detail-settings-button",
                )
                ?.click();
            const autoCollapse = Array.from(
                document.querySelectorAll<HTMLInputElement>(
                    ".vst-timeline-settings-modal input[type='checkbox']",
                ),
            ).find((input) => input.dataset.name === "Auto-collapse");
            if (!autoCollapse) {
                throw new Error("Auto-collapse setting missing");
            }
            autoCollapse.checked = false;
            autoCollapse.dispatchEvent(new Event("change", { bubbles: true }));
            document
                .querySelector<HTMLButtonElement>(
                    ".vst-timeline-settings-modal .modal-header button",
                )
                ?.click();

            const stages = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"]',
            );
            const source = detailBody()?.querySelector<HTMLElement>(
                '[data-vst-accordion-key="source-video"]',
            );
            source
                ?.querySelector<HTMLElement>(":scope > .input-group-header")
                ?.click();
            expect(source?.classList.contains("input-group-open")).toBe(true);
            expect(stages?.classList.contains("input-group-open")).toBe(true);

            renderStrip();
            expect(
                detailBody()
                    ?.querySelector('[data-vst-repeater-key="stages"]')
                    ?.classList.contains("input-group-open"),
            ).toBe(true);
            expect(
                detailBody()
                    ?.querySelector('[data-vst-accordion-key="source-video"]')
                    ?.classList.contains("input-group-open"),
            ).toBe(true);
        });

        it("keeps previously selected repeating item editors open when Auto-collapse is disabled", () => {
            setup([{ duration: 4, stages: [{}, {}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            detail()
                ?.querySelector<HTMLButtonElement>(
                    ".vst-detail-settings-button",
                )
                ?.click();
            const autoCollapse = Array.from(
                document.querySelectorAll<HTMLInputElement>(
                    ".vst-timeline-settings-modal input[type='checkbox']",
                ),
            ).find((input) => input.dataset.name === "Auto-collapse");
            if (!autoCollapse) {
                throw new Error("Auto-collapse setting missing");
            }
            autoCollapse.checked = false;
            autoCollapse.dispatchEvent(new Event("change", { bubbles: true }));
            document
                .querySelector<HTMLButtonElement>(
                    ".vst-timeline-settings-modal .modal-header button",
                )
                ?.click();

            detailBody()
                ?.querySelectorAll<HTMLElement>(
                    '[data-vst-repeater-key="stages"] > .input-group-content > .vst-detail-repeating-group > .input-group-header',
                )[1]
                ?.click();

            const stageGroups = (): HTMLElement[] =>
                Array.from(
                    detailBody()?.querySelectorAll<HTMLElement>(
                        '[data-vst-repeater-key="stages"] > .input-group-content > .vst-detail-repeating-group',
                    ) ?? [],
                );
            expect(
                stageGroups().every((group) =>
                    group.classList.contains("input-group-open"),
                ),
            ).toBe(true);
            expect(
                detailBody()?.querySelectorAll(
                    '[data-vst-repeater-key="stages"] .vst-detail-params',
                ),
            ).toHaveLength(2);

            renderStrip();
            expect(
                stageGroups().every((group) =>
                    group.classList.contains("input-group-open"),
                ),
            ).toBe(true);
            expect(
                detailBody()?.querySelectorAll(
                    '[data-vst-repeater-key="stages"] .vst-detail-params',
                ),
            ).toHaveLength(2);
        });

        it("places every info popover button before its field or section label", () => {
            setup([{ duration: 4, stages: [{}, {}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const buttons = Array.from(
                detailBody()?.querySelectorAll<HTMLElement>(
                    ".info-popover-button",
                ) ?? [],
            );
            expect(buttons.length).toBeGreaterThan(0);
            for (const button of buttons) {
                expect(button.parentElement?.firstElementChild).toBe(button);
            }
        });

        it("keeps the permanent Clip fields visible when its skip button changes", () => {
            setup([{ duration: 4, stages: [{}] }]);
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const clip =
                detailBody()?.querySelector<HTMLElement>(".vst-detail-clip");
            expect(clip).not.toBeNull();
            expect(
                detailBody()?.querySelector('[data-vst-accordion-key="clip"]'),
            ).toBeNull();
            const skip = detailBody()?.querySelector<HTMLButtonElement>(
                ".vst-detail-clip-section > .input-group-header .vst-detail-skip-clip",
            );
            expect(skip?.getAttribute("aria-pressed")).toBe("false");
            skip?.click();

            expect(savedClips(saveSpy)[0].skipped).toBe(true);
            expect(
                detailBody()?.querySelector(".vst-detail-clip"),
            ).not.toBeNull();
            expect(
                detailBody()
                    ?.querySelector(".vst-detail-skip-clip")
                    ?.getAttribute("aria-pressed"),
            ).toBe("true");
            expect(
                detailBody()?.classList.contains("vst-detail-clip-skipped"),
            ).toBe(true);
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
                    ".vst-detail-field",
                ) ?? [],
            );
            const field = fields.find(
                (f) =>
                    f.querySelector(".vst-detail-field-label")?.textContent ===
                    "Overlap",
            );
            return field?.querySelector<HTMLSelectElement>("select") ?? null;
        };
        const carryAudioCheckbox = (): HTMLInputElement | null => {
            const rows = Array.from(
                detailBody()?.querySelectorAll<HTMLElement>(
                    ".vst-detail-field-check",
                ) ?? [],
            );
            const row = rows.find((candidate) =>
                candidate.textContent?.includes(
                    "Continue outgoing audio into next clip",
                ),
            );
            return (
                row?.querySelector<HTMLInputElement>(
                    'input[type="checkbox"]',
                ) ?? null
            );
        };

        it("renders a breadcrumb and join select for the seam", () => {
            setup([
                { duration: 4, stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            expect(crumbText()).toBe("Boundary · Clip 0 → 1");
            expect(boundarySelect().value).toBe("cut");
            expect(
                detailBody()?.querySelector(".vst-detail-boundary"),
            ).not.toBeNull();
            expect(
                detailBody()?.querySelector('[data-vst-static-key="boundary"]'),
            ).not.toBeNull();
            expect(
                detailBody()?.querySelector(
                    '[data-vst-static-key="boundary"] > .input-group-header.input-group-shrinkable',
                ),
            ).toBeNull();
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

        it("offers opt-in outgoing audio carry for an overlapped boundary", () => {
            setup([
                { duration: 4, boundaryOut: "continue", stages: [{}] },
                { duration: 4, stages: [{}] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });
            const checkbox = carryAudioCheckbox();
            expect(checkbox).not.toBeNull();
            expect(checkbox?.checked).toBe(false);

            if (!checkbox) {
                throw new Error("boundary audio carry checkbox missing");
            }
            checkbox.checked = true;
            checkbox.dispatchEvent(new Event("change", { bubbles: true }));

            expect(savedClips(saveSpy)[0].boundaryOutCarryAudio).toBe(true);
            expect(infoText()).toContain(
                "audio tail becomes preserved opening context",
            );
        });

        it("disables audio continuation when the next clip has no generation stage", () => {
            setup([
                { duration: 4, boundaryOut: "crossfade", stages: [{}] },
                { duration: 4, stages: [] },
            ]);
            setSelection({ kind: "boundary", leftClipIdx: 0 });

            expect(carryAudioCheckbox()?.disabled).toBe(true);
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
            expect(carryAudioCheckbox()).toBeNull();
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
                ".vst-detail-settings-button",
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

        it("flushes the active relay before switching editors within the dock", () => {
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
            if (!e0) {
                throw new Error("relay editor missing");
            }
            e0.focus();
            e0.value = "typing in zero";
            e0.dispatchEvent(new Event("input", { bubbles: true }));
            document
                .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
                ?.click();
            expect(savedClips(saveSpy)[0].promptWindows[0].prompt).toBe(
                "typing in zero",
            );
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

        it("rebuilds the selected relay editor when its rail tab changes", () => {
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
            const e0Before = minorEditor(0);
            document
                .querySelectorAll<HTMLButtonElement>(".vst-relay-tab")[1]
                ?.click();
            expect(getSelection()).toEqual({
                kind: "prompt-minor",
                clipIdx: 0,
                windowIdx: 1,
            });
            expect(minorEditor(1)).not.toBe(e0Before);
            expect(document.activeElement).toBe(minorEditor(1));
            expect(minorRows()[0].dataset.vstMinorWindow).toBe("1");
        });
    });

    // ---- #2: native-widget markup + dock-override CSSOM probe -------------
    // The refactor swapped the dock's custom field markup for SwarmUI's native
    // `.auto-input` and `.input-group` are the host's own sidebar vocabulary.
    // The extension intentionally does not override their geometry.

    describe("native sidebar markup (CSSOM probe)", () => {
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
        // load before the extension sheet, matching production.
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
                ".vst-detail .vst-detail-field-check",
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

        it("(b) leaves field geometry to the host's native classes", () => {
            injectHostCss();
            injectDockCss();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 });
            const model = fieldByLabel("Model");
            expect(model.classList.contains("auto-input-flex")).toBe(true);
            expect(computed(model).display).toBe("flex");
            const dropdown = model.querySelector(".auto-dropdown");
            expect(dropdown).not.toBeNull();
            if (dropdown) {
                expect(computed(dropdown).width).toBe("auto");
            }
            const source = fs.readFileSync(
                path.join(__dirname, "..", "Assets", "video-stages.css"),
                "utf8",
            );
            expect(source).not.toMatch(/\.vst-detail\s+\.auto-input\s*\{/);
            expect(source).not.toMatch(/\.vst-detail\s+\.auto-dropdown/);
            expect(computed(detailBody() as HTMLElement).marginBottom).toBe(
                "10px",
            );
            const activeStage = document.querySelector<HTMLElement>(
                '[data-vst-repeater-key="stages"] > .input-group-content > .input-group-open',
            );
            expect(activeStage).not.toBeNull();
            if (activeStage) {
                expect(computed(activeStage).marginBottom).toBe("0px");
            }
        });

        it("(d) wraps prompt textareas in the host's wide text-field row", () => {
            injectHostCss();
            injectDockCss();
            const assertNativePrompt = (): void => {
                const ta = document.querySelector<HTMLElement>(
                    ".vst-detail .vst-detail-prompt",
                );
                expect(ta).not.toBeNull();
                if (!ta) {
                    return;
                }
                expect(computed(ta).width).toBe("100%");
                const row = ta.closest(".auto-input");
                expect(row?.classList.contains("auto-text-box")).toBe(true);
                expect(row?.classList.contains("auto-input-flex-wide")).toBe(
                    true,
                );
                expect(row?.parentElement?.classList).toContain(
                    "input-group-content",
                );
            };

            setSelection({ kind: "prompt-major", clipIdx: 0 });
            assertNativePrompt();
            setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
            assertNativePrompt();
        });

        it("(e) uses native groups for both sections and repeatable items", () => {
            injectHostCss();
            injectDockCss();
            setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });
            const sections = document.querySelectorAll(
                ".vst-detail-body > .vst-detail-section.input-group",
            );
            expect(sections.length).toBeGreaterThan(1);
            expect(
                document.querySelector(
                    ".vst-detail-section .vst-detail-repeating-group.input-group",
                ),
            ).not.toBeNull();
        });

        it("(c) emits the matching native row variant across every panel", () => {
            injectHostCss();
            injectDockCss();
            const panels: (() => void)[] = [
                () => setSelection({ kind: "clip", clipIdx: 0, stageIdx: 1 }),
                () => setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 }),
                () => setSelection({ kind: "audio", clipIdx: 0 }),
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
                for (const field of document.querySelectorAll(
                    ".vst-detail .auto-input",
                )) {
                    expect(
                        field.classList.contains("auto-slider-box") ||
                            field.classList.contains("auto-file-box") ||
                            field.classList.contains("auto-input-flex") ||
                            field.classList.contains("auto-input-flex-wide"),
                    ).toBe(true);
                }
            }
        });
    });
});
