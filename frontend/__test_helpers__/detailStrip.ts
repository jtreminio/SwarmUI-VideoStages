import { afterEach, beforeEach, jest } from "@jest/globals";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { resetIcLoraAutoDownloads } from "../architectures/ltx2/icLoraAutoDownload";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import * as persistence from "../persistence/repository";
import { resetSelectionForTests } from "../selection";
import {
    createTimelineDetailStrip,
    type TimelineDetailStrip,
} from "../timelineDetailStrip";
import { renderTimeline } from "../timelineView";
import type { Clip, ClipReference, InitVideo } from "../types";
import { resetArchitectureCatalogForTests } from "./architectureCatalog";
import {
    testArchitectureCatalog,
    testArchitectureCatalogDto,
} from "./architectureFixtures";
import { initVideoFixture } from "./clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoFps,
    mountVideoStagesData,
} from "./dom";

export interface StageFixture {
    model?: string;
    skipped?: boolean;
    loras?: { name: string; weight: number }[];
    upscale?: number;
    control?: number;
    steps?: number;
}

export interface WindowFixture {
    prompt?: string;
    start: number;
    duration: number;
}

export interface ClipFixture {
    duration: number;
    skipped?: boolean;
    stages: StageFixture[];
    frameRefs?: { source: string; frame: number }[];
    audioSource?: string;
    uploadedAudio?: { data: string; fileName: string };
    controlNetLora?: string;
    icLoras?: Record<string, unknown>[];
    reuseAudio?: boolean;
    clipLengthFromAudio?: boolean;
    prompt?: string;
    windows?: WindowFixture[];
    boundaryOut?: "cut" | "continue" | "crossfade";
    boundaryOutOverlap?: number;
    boundaryOutCarryAudio?: boolean;
    retake?: {
        startSeconds: number;
        lengthSeconds: number;
        strength: number;
    };
    initVideo?: InitVideo;
    references?: Partial<ClipReference>[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    skipped: clip.skipped ?? false,
    boundaryOut: clip.boundaryOut ?? "cut",
    boundaryOutOverlap: clip.boundaryOutOverlap ?? 8,
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
    frameRefs: clip.frameRefs ?? [],
    references: clip.references ?? [],
    promptWindows: [],
    ...(clip.retake ? { retake: clip.retake } : {}),
    ...(clip.initVideo ? { initVideo: clip.initVideo } : {}),
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

const mountRootDefaults = (loras: string[]): void => {
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

/** SwarmUI's LoRA browser globals, which seed a newly added clip LoRA. */
export const modelGlobals = globalThis as unknown as {
    sdLoraBrowser?: {
        models: Record<
            string,
            { data: { lora_default_weight?: string | number } }
        >;
    };
    loraHelper?: {
        loraWeightPref: Record<string, string | number>;
    };
};

/** The SwarmUI globals the IC-LoRA [AUTO] downloader reaches for. */
export const swarmGlobals = globalThis as unknown as {
    makeWSRequest?: jest.Mock;
    refreshParameterValues?: jest.Mock;
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

export const dockHost = (body: HTMLElement): HTMLElement => {
    const dock = body.parentElement?.querySelector<HTMLElement>(".vst-detail");
    if (!dock) {
        throw new Error("dock host not found");
    }
    return dock;
};

export const detail = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-detail");

export const detailBody = (): HTMLElement | null =>
    document.querySelector<HTMLElement>(".vst-detail-body");

export const crumbText = (): string | undefined =>
    detail()?.querySelector<HTMLElement>(".vst-detail-crumb")?.textContent ??
    undefined;

export const railChips = (): HTMLElement[] =>
    Array.from(
        document.querySelectorAll<HTMLElement>(".vst-detail .vst-stage-tab"),
    );

export const activeRailLabel = (): string | undefined =>
    document
        .querySelector<HTMLElement>(
            '.vst-detail .vst-stage-tab[aria-pressed="true"] .header-label',
        )
        ?.textContent?.replace(/^Stage /, "") ?? undefined;

export const sliderNumberByLabel = (label: string): HTMLInputElement => {
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

export const fieldByLabel = (
    label: string,
    scope = ".vst-detail",
): HTMLElement => {
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

/** The committed document, for structural edits that dispatch a named command. */
export const committedClips = (): Clip[] => persistence.getClips();

export const retakeFieldByLabel = (label: string): HTMLElement =>
    fieldByLabel(label, ".vst-detail-retake-col");

/** Retakes are only authorable on a initVideoClip clip (`retake-source-required`). */
export const RETAKE_SOURCE = initVideoFixture({
    durationSeconds: 10,
    lengthSeconds: 10,
});

/** A spinner click / Enter: a `change` while the field still owns focus. */
export const commitNumber = (input: HTMLInputElement, value: string): void => {
    input.focus();
    input.value = value;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
};

export const refRow = (idx: number): HTMLElement => {
    const row = document.querySelector<HTMLElement>(
        `.vst-detail-ref-row[data-vst-ref-index="${idx}"]`,
    );
    if (!row) {
        throw new Error(`ref row ${idx} missing`);
    }
    return row;
};

export const minorRows = (): HTMLElement[] =>
    Array.from(
        document.querySelectorAll<HTMLElement>(".vst-detail-minor-window"),
    );

export const minorEditor = (idx: number): HTMLTextAreaElement => {
    const ta = document.querySelector<HTMLTextAreaElement>(
        `textarea[data-vst-focus-key="minor-${idx}"]`,
    );
    if (!ta) {
        throw new Error(`minor editor ${idx} missing`);
    }
    return ta;
};

export interface DetailStripHarness {
    /** Mounts the fixtures, renders the timeline and attaches a fresh strip. */
    setup(fixtures: ClipFixture[], loras?: string[]): HTMLElement;
    renderStrip(): void;
    clickRegionStageChip(
        body: HTMLElement,
        clipIdx: number,
        stageIdx: number,
        shift?: boolean,
    ): void;
    readonly strip: TimelineDetailStrip;
    readonly saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;
    /**
     * Fires on every store notification — the signal that drives the
     * orchestrator's timeline repaint in production.
     */
    readonly refreshSpy: jest.Mock;
    disposeStrip(): void;
}

/**
 * The whole-strip integration environment: one catalog-backed dock over a real
 * timeline body, torn down between tests. Registers its own jest hooks, so call
 * it once inside the describe that uses it.
 */
export const detailStripHarness = (): DetailStripHarness => {
    let strip: TimelineDetailStrip | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;
    let refreshSpy: jest.Mock;

    beforeEach(async () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities.audioSourceKinds = [
            "Native",
            "Upload",
            "ControlNet",
            "AceStepFun",
        ];
        catalog.entries.push({
            ...catalog.entries[0],
            value: "ltx-2.3-alt.safetensors",
            label: "LTX 2.3 Alt",
        });
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(catalog),
        });
        await loadAuthoritativeArchitectureCatalog();
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
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
        resetSelectionForTests();
        document.body.innerHTML = "";
        delete modelGlobals.sdLoraBrowser;
        delete modelGlobals.loraHelper;
        delete swarmGlobals.makeWSRequest;
        delete swarmGlobals.refreshParameterValues;
    });

    const requireStrip = (): TimelineDetailStrip => {
        if (!strip) {
            throw new Error("detail strip is not initialized");
        }
        return strip;
    };

    return {
        setup: (fixtures, loras = ["lora-x.safetensors"]) => {
            mountPromptBox(promptText(fixtures));
            mountRootDefaults(loras);
            mountVideoStagesData({ clips: fixtures.map(clipRecord) });
            const body = makeBody();
            renderTimeline(body, persistence.getClips());
            refreshSpy = jest.fn();
            strip = createTimelineDetailStrip();
            persistence.getTimelineStore().subscribe(() => refreshSpy());
            strip.attach(body, dockHost(body));
            return body;
        },
        renderStrip: () => requireStrip().render(),
        clickRegionStageChip: (body, clipIdx, stageIdx, shift = false) => {
            const chip = body.querySelector<HTMLElement>(
                `[data-vst-stage][data-clip-idx="${clipIdx}"][data-stage-idx="${stageIdx}"]`,
            );
            if (!chip) {
                throw new Error(`stage chip not found: ${clipIdx}/${stageIdx}`);
            }
            chip.dispatchEvent(
                new MouseEvent("click", { bubbles: true, shiftKey: shift }),
            );
        },
        get strip() {
            return requireStrip();
        },
        get saveSpy() {
            return saveSpy;
        },
        get refreshSpy() {
            return refreshSpy;
        },
        disposeStrip: () => {
            strip?.dispose();
            strip = null;
        },
    };
};
