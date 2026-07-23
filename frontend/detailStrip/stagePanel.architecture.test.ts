import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
    testRootDefaults,
} from "../__test_helpers__/architectureFixtures";
import { minimalClip, minimalStage } from "../__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "../__test_helpers__/dom";
import { buildArchitectureIcLorasSection } from "../architectures/authoringPanels";
import {
    __resetArchitectureCatalogForTests,
    loadAuthoritativeArchitectureCatalog,
} from "../architectures/catalog";
import { createCapabilityViewResolver } from "../architectures/policy";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import {
    __resetPersistenceForTests,
    getState,
    getTimelineStore,
    saveState,
} from "../persistence";
import { createTimelineHistory } from "../timelineHistory";
import type { DetailStripContext } from "./context";
import { buildStageParamsColumn } from "./stagePanel";

const catalog = (): ArchitectureModelCatalog => {
    const ltx = testArchitectureCatalog();
    const fake = fakeArchitectureCatalog();
    return {
        source: "backend",
        architectures: [...ltx.architectures, ...fake.architectures],
        entries: [...ltx.entries, ...fake.entries],
    };
};

const context = (
    models = catalog(),
    generatedEntryMode: "text-to-video" | "image-to-video" = "text-to-video",
): DetailStripContext => ({
    commit: jest.fn(),
    commitState: jest.fn(),
    debouncedCommit: jest.fn(),
    debouncedCommitState: jest.fn(),
    buildClampedNumber: () => document.createElement("input"),
    structuralCommit: jest.fn(),
    render: jest.fn(),
    deleteRefEntry: jest.fn(),
    deleteWindowEntry: jest.fn(),
    createRetake: jest.fn(),
    removeRetake: jest.fn(),
    addAudioSegment: jest.fn(),
    removeAudioSegment: jest.fn(),
    addStage: jest.fn(),
    deleteStage: jest.fn(),
    selectStage: jest.fn(),
    getBoundBody: () => null,
    getDockEl: () => null,
    getSettingsMode: () => null,
    setSettingsMode: jest.fn(),
    capabilities: () => createCapabilityViewResolver(models),
    generatedEntryMode: () => generatedEntryMode,
});

const modelOptions = (column: HTMLElement): HTMLOptionElement[] => {
    const modelField = Array.from(
        column.querySelectorAll<HTMLElement>(".auto-input"),
    ).find((field) =>
        field.querySelector("label")?.textContent?.includes("Model"),
    );
    return Array.from(modelField?.querySelectorAll("option") ?? []);
};

afterEach(() => {
    jest.restoreAllMocks();
    __resetArchitectureCatalogForTests();
    setVideoStagesHostBridgeForTests(null);
    __resetPersistenceForTests();
    document.body.innerHTML = "";
});

describe("stage architecture model filtering", () => {
    it("keeps unsupported persisted sampling and normal-LoRA values visible for repair", () => {
        const models = catalog();
        const stage = minimalStage({
            sampler: "removed-sampler",
            scheduler: "removed-scheduler",
            loras: [{ name: "removed-lora.safetensors", weight: 1 }],
        });
        const clip = minimalClip({ stages: [stage] });
        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            0,
            stage,
            testRootDefaults(models),
        );
        const option = (value: string) =>
            Array.from(column.querySelectorAll("option")).find(
                (entry) => entry.value === value,
            );

        expect(option("removed-sampler")).toMatchObject({ disabled: true });
        expect(option("removed-scheduler")).toMatchObject({ disabled: true });
        expect(option("removed-lora.safetensors")).toMatchObject({
            disabled: true,
        });
    });

    it("keeps an unsupported persisted LTX IC-LoRA weight visible for repair", () => {
        const models = catalog();
        const clip = minimalClip({
            icLoras: [
                {
                    lora: "removed-ic-lora.safetensors",
                    preset: "custom",
                    source: "Upload",
                    stage: -1,
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    driveMedia: null,
                },
            ],
        });
        const panel = buildArchitectureIcLorasSection(
            context(models),
            clip,
            0,
            testRootDefaults(models),
        );
        const persisted = Array.from(panel.querySelectorAll("option")).find(
            (entry) => entry.value === "removed-ic-lora.safetensors",
        );

        expect(persisted).toMatchObject({ disabled: true });
        expect(persisted?.textContent).toContain("unsupported persisted value");
    });

    it("offers every supported architecture model on stage 0", () => {
        const models = catalog();
        const clip = minimalClip({
            stages: [minimalStage({ model: "ltx" })],
        });

        const column = buildStageParamsColumn(
            context(),
            clip,
            0,
            0,
            clip.stages[0],
            testRootDefaults(models),
        );

        expect(modelOptions(column).map((option) => option.value)).toEqual([
            "ltx-2.3.safetensors",
            "ltx",
            "test-video.safetensors",
        ]);
    });

    it("locks later stages to the clip architecture", () => {
        const models = catalog();
        const clip = minimalClip({
            stages: [
                minimalStage({ model: "ltx" }),
                minimalStage({ model: "ltx" }),
            ],
        });

        const column = buildStageParamsColumn(
            context(),
            clip,
            0,
            1,
            clip.stages[1],
            testRootDefaults(models),
        );

        expect(modelOptions(column).map((option) => option.value)).toEqual([
            "ltx-2.3.safetensors",
            "ltx",
        ]);
    });

    it("keeps an invalid persisted later-stage model visible for repair", () => {
        const models = catalog();
        const invalid = minimalStage({
            model: "test-video.safetensors",
            modelProfileId: "test-profile",
        });
        const clip = minimalClip({
            stages: [minimalStage({ model: "ltx" }), invalid],
        });

        const column = buildStageParamsColumn(
            context(),
            clip,
            0,
            1,
            invalid,
            testRootDefaults(models),
        );
        const options = modelOptions(column);

        expect(options.map((option) => option.value)).toEqual([
            "test-video.safetensors",
            "ltx-2.3.safetensors",
            "ltx",
        ]);
        expect(options[0].textContent).toContain("unsupported persisted value");
    });

    it("excludes a text-only architecture from a source-video start", () => {
        const models = catalog();
        const fake = models.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!fake) throw new Error("missing fake architecture");
        fake.capabilities.entryModes = ["text-to-video"];
        const clip = minimalClip({
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [minimalStage({ model: "ltx" })],
        });

        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            0,
            clip.stages[0],
            testRootDefaults(models),
        );

        expect(modelOptions(column).map((option) => option.value)).toEqual([
            "ltx-2.3.safetensors",
            "ltx",
        ]);
    });

    it("keeps an entry-incompatible persisted stage-0 model read-only", () => {
        const models = catalog();
        const fake = models.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!fake) throw new Error("missing fake architecture");
        fake.capabilities.entryModes = ["text-to-video"];
        const persisted = minimalStage({
            model: "test-video.safetensors",
            modelProfileId: "test-profile",
        });
        const clip = minimalClip({
            architecture: "test-video",
            modelProfileId: "test-profile",
            sourceVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [persisted],
        });

        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            0,
            persisted,
            testRootDefaults(models),
        );
        const options = modelOptions(column);

        expect(options[0]).toMatchObject({
            value: "test-video.safetensors",
            disabled: true,
        });
        expect(options[0].textContent).toContain("unsupported persisted value");
    });

    it("restores the model selection when architecture conversion is canceled", () => {
        const models = catalog();
        const clip = minimalClip({
            stages: [minimalStage({ model: "ltx" })],
        });
        const confirm = jest.spyOn(window, "confirm").mockReturnValue(false);
        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            0,
            clip.stages[0],
            testRootDefaults(models),
        );
        const select = modelOptions(column)[0].parentElement;
        if (!(select instanceof HTMLSelectElement)) {
            throw new Error("missing model select");
        }

        select.value = "test-video.safetensors";
        select.dispatchEvent(new Event("change"));

        expect(confirm).toHaveBeenCalledWith(
            expect.stringContaining("one undoable change"),
        );
        expect(select.value).toBe("ltx");
    });

    it("commits a confirmed conversion through the UI as one exact undoable store change", async () => {
        const models = catalog();
        const dto = {
            architectures: structuredClone(models.architectures),
            models: models.entries.map((entry) => ({
                modelName: entry.value,
                architectureId: entry.architectureId as string,
                modelProfileId: entry.modelProfileId as string,
                compatId: entry.compatId,
            })),
        };
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => dto,
        });
        await loadAuthoritativeArchitectureCatalog();

        const source = minimalClip({
            prompt: "keep this prompt",
            stages: [minimalStage({ model: "ltx" })],
        });
        mountVideoStagesData({ clips: [source], audioTracks: [] });
        mountPromptBox("");
        mountSelect("input_videomodel", {
            value: "ltx",
            options: ["ltx", "test-video.safetensors"],
        });
        __resetPersistenceForTests();

        const notifications: string[] = [];
        const history = createTimelineHistory({
            read: () => JSON.stringify(getState()),
            write: (value) => {
                saveState(JSON.parse(value) as ReturnType<typeof getState>, {
                    expectedRevision: getTimelineStore().revision(),
                    notifyDomChange: false,
                    origin: "history",
                });
            },
        });
        history.syncBaseline();
        getTimelineStore().subscribe((_state, meta) => {
            notifications.push(meta.origin);
            history.capture();
        });

        const before = JSON.stringify(getState());
        const revisionBefore = getTimelineStore().revision();
        const confirm = jest.spyOn(window, "confirm").mockReturnValue(true);
        const panelContext = context(models);
        const persisted = getState().clips[0];
        const column = buildStageParamsColumn(
            panelContext,
            persisted,
            0,
            0,
            persisted.stages[0],
            testRootDefaults(models),
        );
        const select = modelOptions(column)[0].parentElement;
        if (!(select instanceof HTMLSelectElement)) {
            throw new Error("missing model select");
        }

        select.value = "test-video.safetensors";
        select.dispatchEvent(new Event("change"));

        expect(confirm).toHaveBeenCalledTimes(1);
        expect(getTimelineStore().revision()).toBe(revisionBefore + 1);
        expect(notifications).toEqual(["detail-strip"]);
        expect(panelContext.render).toHaveBeenCalledTimes(1);
        expect(getState().clips[0]).toMatchObject({
            architecture: "test-video",
            modelProfileId: "test-profile",
            stages: [
                {
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                },
            ],
        });
        const after = JSON.stringify(getState());

        expect(history.undo()).toBe(true);
        expect(JSON.stringify(getState())).toBe(before);
        expect(history.undo()).toBe(false);
        expect(history.redo()).toBe(true);
        expect(JSON.stringify(getState())).toBe(after);
        expect(history.redo()).toBe(false);
        expect(notifications).toEqual(["detail-strip", "history", "history"]);
    });

    it("disables sampler and scheduler controls when the profile omits those capabilities", () => {
        const models = catalog();
        const stage = minimalStage({
            model: "test-video.safetensors",
            modelProfileId: "test-profile",
        });
        const clip = minimalClip({
            architecture: "test-video",
            modelProfileId: "test-profile",
            stages: [stage],
        });
        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            0,
            stage,
            testRootDefaults(models),
        );
        const field = (label: string) =>
            Array.from(
                column.querySelectorAll<HTMLElement>(".auto-input"),
            ).find((candidate) =>
                candidate.querySelector("label")?.textContent?.includes(label),
            );

        expect(field("Sampler")?.querySelector("select")?.disabled).toBe(true);
        expect(field("Scheduler")?.querySelector("select")?.disabled).toBe(
            true,
        );
    });

    it("separates pure-text and host-image model choices in both directions", () => {
        const models = catalog();
        const ltx = models.architectures.find((entry) => entry.id === "ltx2");
        const fake = models.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!ltx || !fake) throw new Error("missing test architectures");
        ltx.capabilities.entryModes = ["image-to-video"];
        fake.capabilities.entryModes = ["text-to-video"];
        const textStage = minimalStage({
            model: "test-video.safetensors",
            modelProfileId: "test-profile",
        });
        const textClip = minimalClip({
            architecture: "test-video",
            modelProfileId: "test-profile",
            stages: [textStage],
        });
        const imageStage = minimalStage({ model: "ltx" });
        const imageClip = minimalClip({ stages: [imageStage] });

        const textColumn = buildStageParamsColumn(
            context(models, "text-to-video"),
            textClip,
            0,
            0,
            textStage,
            testRootDefaults(models),
        );
        const imageColumn = buildStageParamsColumn(
            context(models, "image-to-video"),
            imageClip,
            0,
            0,
            imageStage,
            testRootDefaults(models),
        );

        expect(modelOptions(textColumn).map(({ value }) => value)).toEqual([
            "test-video.safetensors",
        ]);
        expect(modelOptions(imageColumn).map(({ value }) => value)).toEqual([
            "ltx-2.3.safetensors",
            "ltx",
        ]);
    });
});
