import { afterEach, beforeEach, jest } from "@jest/globals";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import { resetIcLoraAutoDownloads } from "../architectures/ltx2/icLoraAutoDownload";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import * as persistence from "../persistence/repository";
import { resetSelectionForTests } from "../selection";
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
    audioOnly?: boolean;
    stages: StageFixture[];
    frameRefs?: { source: string; frame: number }[];
    audioSource?: string;
    uploadedAudio?: { data: string; fileName: string };
    uploadedAudioDurationSeconds?: number;
    uploadedAudioStartSeconds?: number;
    uploadedAudioLengthSeconds?: number;
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

const clipRecord = (clip: ClipFixture): Record<string, unknown> => {
    return {
        duration: clip.duration,
        skipped: clip.skipped ?? false,
        audioOnly: clip.audioOnly ?? false,
        boundaryOut: clip.boundaryOut ?? "cut",
        boundaryOutOverlap: clip.boundaryOutOverlap ?? 8,
        boundaryOutCarryAudio: clip.boundaryOutCarryAudio ?? false,
        audioSource: clip.audioSource ?? "Native",
        ...(clip.uploadedAudio ? { uploadedAudio: clip.uploadedAudio } : {}),
        ...(clip.uploadedAudioDurationSeconds === undefined
            ? {}
            : {
                  uploadedAudioDurationSeconds:
                      clip.uploadedAudioDurationSeconds,
                  uploadedAudioStartSeconds:
                      clip.uploadedAudioStartSeconds ?? 0,
                  uploadedAudioLengthSeconds:
                      clip.uploadedAudioLengthSeconds ?? 0,
              }),
        controlNetLora: clip.controlNetLora ?? "",
        ...(clip.icLoras ? { icLoras: clip.icLoras } : {}),
        reuseAudio: clip.reuseAudio ?? false,
        clipLengthFromAudio: clip.clipLengthFromAudio ?? false,
        stages: clip.stages.map((stage) => ({
            loras: stage.loras ?? [],
            model: stage.model ?? "ltx-2.3.safetensors",
            skipped: stage.skipped ?? false,
            upscale: stage.upscale,
            control: stage.control,
            steps: stage.steps,
        })),
        frameRefs: clip.frameRefs ?? [],
        references: clip.references ?? [],
        promptWindows: [],
        ...(clip.retake ? { retake: clip.retake } : {}),
        ...(clip.initVideo ? { initVideo: clip.initVideo } : {}),
    };
};

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

/** SwarmUI metadata and preferences seed new LoRA strengths. */
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

/** Host globals used by the IC-LoRA downloader. */
export const swarmGlobals = globalThis as unknown as {
    makeWSRequest?: jest.Mock;
    refreshParameterValues?: jest.Mock;
};

export const mountAuthoringFixtures = (
    fixtures: ClipFixture[],
    loras = ["lora-x.safetensors"],
): void => {
    mountPromptBox(promptText(fixtures));
    mountRootDefaults(loras);
    mountVideoStagesData({ clips: fixtures.map(clipRecord) });
};

export const authoringFixtureHarness = (dispose?: () => void) => {
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;
    let refreshSpy: jest.Mock;
    let unsubscribe: (() => void) | null = null;

    beforeEach(async () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities.audioSourceKinds = [
            "Native",
            "Upload",
            "ControlNet",
            "AceStepFun",
            "Previous Clip",
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
            createInitVideoElement: () => {
                const video = document.createElement("video");
                video.pause = jest.fn();
                video.load = jest.fn();
                video.play = jest.fn(() => Promise.resolve());
                return video;
            },
        });
        await loadAuthoritativeArchitectureCatalog();
        resetSelectionForTests();
        persistence.__resetPersistenceForTests();
        resetIcLoraAutoDownloads();
        localStorage.clear();
        saveSpy = jest.spyOn(persistence, "saveClips");
    });

    afterEach(() => {
        dispose?.();
        unsubscribe?.();
        unsubscribe = null;
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

    return {
        setupFixtures: (fixtures: ClipFixture[], loras?: string[]): void => {
            mountAuthoringFixtures(fixtures, loras);
            refreshSpy = jest.fn();
            unsubscribe?.();
            unsubscribe = persistence
                .getTimelineStore()
                .subscribe(() => refreshSpy());
        },
        get saveSpy() {
            return saveSpy;
        },
        get refreshSpy() {
            return refreshSpy;
        },
    };
};

export const committedClips = (): Clip[] => persistence.getClips();

/** Retakes require source video. */
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
