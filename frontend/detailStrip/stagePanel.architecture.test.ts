import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "../__test_helpers__/architectureCatalog";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
    testAuthoringTransactionSnapshot,
    testRootDefaults,
} from "../__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "../__test_helpers__/dom";
import { buildArchitectureIcLorasSection } from "../architectures/authoringPanels";
import { loadAuthoritativeArchitectureCatalog } from "../architectures/catalog";
import type { GeneratedEntryMode } from "../architectures/generatedFeatures";
import type { ArchitectureModelCatalog } from "../architectures/types";
import { setVideoStagesHostBridgeForTests } from "../host";
import { createDefaultVideoStagesHostBridge } from "../host/defaultVideoStagesHostBridge";
import * as persistence from "../persistence/repository";
import {
    __resetPersistenceForTests,
    getState,
    getTimelineStore,
    saveState,
} from "../persistence/repository";
import { createTimelineHistory } from "../timelineHistory";
import { buildClipLorasSection } from "./clipLorasPanel";
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

const catalogWithWan = (): ArchitectureModelCatalog => {
    const models = catalog();
    const wan = structuredClone(models.architectures[0]);
    wan.id = "wan22";
    wan.label = "WAN";
    wan.capabilities.features = wan.capabilities.features.filter(
        (feature) =>
            feature !== "latentUpscale" && feature !== "latentModelUpscale",
    );
    models.architectures.push(wan);
    models.entries.push(
        {
            value: "wan-current.safetensors",
            label: "WAN Current",
            architectureId: "wan22",
            modelProfileId: "wan-i2v",
            modelClassId: "wan-current",
            compatibilityClassId: "wan-video",
            entryModes: ["text-to-video", "image-to-video", "init-video"],
        },
        {
            value: "wan-alternate.safetensors",
            label: "WAN Alternate",
            architectureId: "wan22",
            modelProfileId: "wan-i2v",
            modelClassId: "wan-alternate",
            compatibilityClassId: "wan-video",
            entryModes: ["text-to-video", "image-to-video", "init-video"],
        },
    );
    return models;
};

const context = (
    models = catalog(),
    generatedEntryMode: GeneratedEntryMode = "text-to-video",
): DetailStripContext => ({
    commit: jest.fn(),
    commitState: jest.fn(),
    debouncedCommit: jest.fn(),
    debouncedCommitState: jest.fn(),
    buildClampedNumber: () => document.createElement("input"),
    structuralCommit: jest.fn(),
    render: jest.fn(),
    addRefEntry: jest.fn(),
    deleteRefEntry: jest.fn(),
    addClipReference: jest.fn(),
    deleteClipReference: jest.fn(),
    addPromptWindow: jest.fn(),
    deleteWindowEntry: jest.fn(),
    createRetake: jest.fn(),
    removeRetake: jest.fn(),
    addStage: jest.fn(),
    deleteStage: jest.fn(),
    selectStage: jest.fn(),
    toggleClipSkip: jest.fn(),
    toggleStageSkip: jest.fn(),
    getBoundBody: () => null,
    getDockEl: () => null,
    getSettingsMode: () => null,
    setSettingsMode: jest.fn(),
    authoring: () =>
        testAuthoringTransactionSnapshot(models, generatedEntryMode),
});

const upscaleMethodOptions = (column: HTMLElement): string[] => {
    const field = Array.from(
        column.querySelectorAll<HTMLElement>(".auto-input"),
    ).find((entry) =>
        entry.querySelector("label")?.textContent?.includes("Upscale Method"),
    );
    return Array.from(field?.querySelectorAll("option") ?? []).map(
        (option) => option.value,
    );
};

const modelOptions = (column: HTMLElement): HTMLOptionElement[] => {
    const modelField = Array.from(
        column.querySelectorAll<HTMLElement>(".auto-input"),
    ).find((field) =>
        field.querySelector("label")?.textContent?.includes("Model"),
    );
    return Array.from(modelField?.querySelectorAll("option") ?? []);
};

/** Puts the clip in the store so a model change has something to commit against. */
const mountClip = <T extends object>(clip: T): T => {
    mountVideoStagesData({ clips: [clip], audioTracks: [] });
    mountPromptBox("");
    __resetPersistenceForTests();
    return getState().clips[0] as T;
};

/** Which command a model change dispatches is the whole-clip vs stage decision. */
const dispatchSpy = () =>
    jest
        .spyOn(persistence, "dispatchDocumentCommand")
        .mockReturnValue({ applied: false, revision: 0 });

afterEach(() => {
    jest.restoreAllMocks();
    resetArchitectureCatalogForTests();
    setVideoStagesHostBridgeForTests(null);
    __resetPersistenceForTests();
    document.body.innerHTML = "";
});

describe("stage architecture model filtering", () => {
    it("keeps every WAN family model available for text and image roots", () => {
        const models = catalog();
        const wan = structuredClone(models.architectures[0]);
        wan.id = "wan22";
        wan.label = "WAN 2.2";
        models.architectures.push(wan);
        models.entries.push(
            {
                value: "wan-14b.safetensors",
                label: "WAN 14B",
                architectureId: "wan22",
                modelProfileId: "wan22-i2v-14b",
                modelClassId: "wan-i2v-14b",
                compatibilityClassId: "wan-video",
                entryModes: ["image-to-video", "init-video"],
            },
            {
                value: "wan-5b.safetensors",
                label: "WAN 5B",
                architectureId: "wan22",
                modelProfileId: "wan22-ti2v-5b",
                modelClassId: "wan-ti2v-5b",
                compatibilityClassId: "wan-video",
                entryModes: ["text-to-video", "image-to-video"],
            },
        );
        const stage = minimalStage({ model: "ltx" });
        const optionsFor = (
            entryMode: GeneratedEntryMode,
            initVideo = false,
            initialReference = false,
        ) => {
            const clip = minimalClip({
                frameRefs: initialReference ? [minimalRef({ frame: 1 })] : [],
                initVideo: initVideo
                    ? {
                          data: "data:video/mp4;base64,AA==",
                          fileName: "source.mp4",
                          fps: 24,
                          durationSeconds: 2,
                          startSeconds: 0,
                          lengthSeconds: 2,
                      }
                    : null,
                stages: [stage],
            });
            const column = buildStageParamsColumn(
                context(models, entryMode),
                clip,
                0,
                0,
                stage,
                testRootDefaults(models),
            );
            return modelOptions(column)
                .map((option) => option.value)
                .filter((value) => value.startsWith("wan-"));
        };

        expect(optionsFor("text-to-video")).toEqual([
            "wan-14b.safetensors",
            "wan-5b.safetensors",
        ]);
        expect(optionsFor("text-to-video", false, true)).toEqual([
            "wan-14b.safetensors",
            "wan-5b.safetensors",
        ]);
        expect(optionsFor("image-to-video")).toEqual([
            "wan-14b.safetensors",
            "wan-5b.safetensors",
        ]);
        expect(optionsFor("image-to-video", true)).toEqual([
            "wan-14b.safetensors",
            "wan-5b.safetensors",
        ]);

        const laterOptionsFor = (
            entryMode: GeneratedEntryMode,
            initVideo = false,
            persistedModel = "wan-5b.safetensors",
        ) => {
            const laterStage = minimalStage({
                model: persistedModel,
                modelProfileId:
                    persistedModel === "wan-14b.safetensors"
                        ? "wan22-i2v-14b"
                        : "wan22-ti2v-5b",
            });
            const clip = minimalClip({
                architectureHint: "wan22",
                modelProfileId: "wan22-ti2v-5b",
                initVideo: initVideo
                    ? {
                          data: "data:video/mp4;base64,AA==",
                          fileName: "source.mp4",
                          fps: 24,
                          durationSeconds: 2,
                          startSeconds: 0,
                          lengthSeconds: 2,
                      }
                    : null,
                stages: [
                    minimalStage({
                        model: "wan-5b.safetensors",
                        modelProfileId: "wan22-ti2v-5b",
                    }),
                    laterStage,
                ],
            });
            const column = buildStageParamsColumn(
                context(models, entryMode),
                clip,
                0,
                1,
                laterStage,
                testRootDefaults(models),
            );
            return modelOptions(column).filter((option) =>
                option.value.startsWith("wan-"),
            );
        };

        expect(
            laterOptionsFor("text-to-video").map(({ value }) => value),
        ).toEqual(["wan-14b.safetensors", "wan-5b.safetensors"]);
        expect(
            laterOptionsFor("image-to-video").map(({ value }) => value),
        ).toEqual(["wan-14b.safetensors", "wan-5b.safetensors"]);
        expect(
            laterOptionsFor("image-to-video", true).map(({ value }) => value),
        ).toEqual(["wan-14b.safetensors", "wan-5b.safetensors"]);
        const persisted14b = laterOptionsFor(
            "text-to-video",
            false,
            "wan-14b.safetensors",
        );
        expect(persisted14b.map(({ value }) => value)).toEqual([
            "wan-14b.safetensors",
            "wan-5b.safetensors",
        ]);
        expect(persisted14b[0]).toMatchObject({ disabled: false });
    });

    it("keeps unsupported persisted sampling and normal-LoRA values visible for repair", () => {
        const models = catalog();
        const stage = minimalStage({
            sampler: "removed-sampler",
            scheduler: "removed-scheduler",
            loraWeights: [1],
        });
        const clip = minimalClip({
            loras: [{ name: "removed-lora.safetensors" }],
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
        column.appendChild(
            buildClipLorasSection(
                context(models),
                clip,
                0,
                0,
                testRootDefaults(models),
            ),
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
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["image", "video"],
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

    it("uses the removal-only IC-LoRA panel when Stage 0 is unresolved", () => {
        const models = catalog();
        const clip = minimalClip({
            architectureHint: "ltx2",
            icLoras: [
                {
                    lora: "persisted-ic-lora.safetensors",
                    preset: "custom",
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["image", "video"],
                    stage: -1,
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    driveMedia: null,
                },
            ],
            stages: [
                minimalStage({
                    model: "removed-model.safetensors",
                    modelProfileId: "removed-profile",
                }),
            ],
        });

        const panel = buildArchitectureIcLorasSection(
            context(models),
            clip,
            0,
            testRootDefaults(models),
        );
        const add = panel.querySelector<HTMLButtonElement>(
            ".vst-detail-add-iclora",
        );

        expect(panel.textContent).toContain("Persisted IC-LoRAs");
        expect(panel.textContent).toContain(
            "Existing entries remain available for removal.",
        );
        expect(add?.disabled).toBe(true);
        expect(panel.querySelector("select")).toBeNull();
    });

    it("offers each latent upscaling mode only when the architecture supports it", () => {
        const models = catalogWithWan();
        const defaults = {
            ...testRootDefaults(models),
            upscaleMethodValues: [
                "pixel-lanczos",
                "model-ultrasharp.safetensors",
                "latent-nearest-exact",
                "latentmodel-detail.safetensors",
            ],
            upscaleMethodLabels: [
                "Lanczos",
                "UltraSharp",
                "Latent Nearest",
                "Latent Detail",
            ],
        };
        const methodsFor = (
            model: string,
            upscaleMethod = "pixel-lanczos",
        ): string[] => {
            const clip = minimalClip({
                stages: [
                    minimalStage({ model, upscaleMethod }),
                    minimalStage({ model, upscaleMethod }),
                ],
            });
            return upscaleMethodOptions(
                buildStageParamsColumn(
                    context(models),
                    clip,
                    0,
                    1,
                    clip.stages[1],
                    defaults,
                ),
            );
        };

        const pixelMethods = ["pixel-lanczos", "model-ultrasharp.safetensors"];
        expect(methodsFor("ltx")).toEqual([
            ...pixelMethods,
            "latent-nearest-exact",
            "latentmodel-detail.safetensors",
        ]);
        expect(methodsFor("wan-current.safetensors")).toEqual(pixelMethods);
        expect(
            methodsFor("wan-current.safetensors", "latent-nearest-exact"),
        ).toEqual(["latent-nearest-exact", ...pixelMethods]);
        const wan = models.architectures.find((entry) => entry.id === "wan22");
        if (!wan) throw new Error("missing WAN architecture");
        wan.capabilities.features.push("latentUpscale");
        expect(methodsFor("wan-current.safetensors")).toEqual([
            ...pixelMethods,
            "latent-nearest-exact",
        ]);
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

    it("locks later stages to the clip architecture and compatibility class", () => {
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

    it.each([
        ["active", false],
        ["skipped", true],
    ])("hides a same-architecture root retarget that would conflict with another %s authored stage", (_label, skipped) => {
        const models = catalog();
        models.entries.push({
            ...models.entries[0],
            value: "ltx-other-family.safetensors",
            label: "LTX Other Family",
            modelProfileId: "ltx-other-family",
            modelClassId: "ltx-other-family",
            compatibilityClassId: "other-ltx-family",
        });
        const clip = minimalClip({
            stages: [
                minimalStage(),
                minimalStage({
                    model: "ltx",
                    skipped,
                }),
            ],
        });

        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            0,
            clip.stages[0],
            testRootDefaults(models),
        );

        expect(modelOptions(column).map(({ value }) => value)).not.toContain(
            "ltx-other-family.safetensors",
        );
    });

    it("shows an image-only target because source video remains authored", () => {
        const models = catalog();
        const target = models.entries.find(
            (entry) => entry.value === "test-video.safetensors",
        );
        if (!target) {
            throw new Error("missing test video model");
        }
        target.entryModes = ["image-to-video"];
        const clip = minimalClip({
            initVideo: {
                data: "data:video/mp4;base64,AA==",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
        });
        const before = structuredClone(clip);

        const column = buildStageParamsColumn(
            context(models, "text-to-video"),
            clip,
            0,
            0,
            clip.stages[0],
            testRootDefaults(models),
        );

        expect(modelOptions(column).map(({ value }) => value)).toContain(
            "test-video.safetensors",
        );
        expect(clip).toEqual(before);
    });

    it("locks the first active stage to a skipped Stage 0 compatibility class", () => {
        const models = catalog();
        models.entries.push({
            ...models.entries[0],
            value: "ltx-other-family.safetensors",
            label: "LTX Other Family",
            modelProfileId: "ltx-other-family",
            modelClassId: "ltx-other-family",
            compatibilityClassId: "other-ltx-family",
        });
        const active = minimalStage({ model: "ltx" });
        const clip = minimalClip({
            stages: [minimalStage({ skipped: true }), active],
        });

        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            1,
            active,
            testRootDefaults(models),
        );

        expect(modelOptions(column).map(({ value }) => value)).not.toContain(
            "ltx-other-family.safetensors",
        );
    });

    it("keeps a persisted later-stage model from another compatibility class visible but disabled", () => {
        const models = catalog();
        const incompatible = models.entries.find(
            (entry) => entry.value === "ltx",
        );
        if (!incompatible) throw new Error("missing LTX alias");
        incompatible.compatibilityClassId = "other-ltx-family";
        const stage = minimalStage({ model: "ltx" });
        const clip = minimalClip({
            stages: [minimalStage({ model: "ltx-2.3.safetensors" }), stage],
        });

        const column = buildStageParamsColumn(
            context(models),
            clip,
            0,
            1,
            stage,
            testRootDefaults(models),
        );
        const options = modelOptions(column);

        expect(options.map(({ value }) => value)).toEqual([
            "ltx",
            "ltx-2.3.safetensors",
        ]);
        expect(options[0]).toMatchObject({ disabled: true });
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

    it("offers whole-clip repair when Stage 0 and sibling models are unresolved", () => {
        const models = catalog();
        const clip = minimalClip({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [
                minimalStage({ model: "removed-root.safetensors" }),
                minimalStage({ model: "removed-sibling.safetensors" }),
            ],
        });
        const persisted = mountClip(clip);
        const dispatch = dispatchSpy();
        const column = buildStageParamsColumn(
            context(models),
            persisted,
            0,
            0,
            persisted.stages[0],
            testRootDefaults(models),
        );
        const options = modelOptions(column);
        const select = options[0].parentElement;
        if (!(select instanceof HTMLSelectElement)) {
            throw new Error("missing model select");
        }

        expect(options.map(({ value }) => value)).toEqual(
            expect.arrayContaining(["ltx", "test-video.safetensors"]),
        );
        select.value = "ltx";
        select.dispatchEvent(new Event("change"));
        expect(dispatch).toHaveBeenCalledWith(
            expect.objectContaining({ type: "clip.convert-architecture" }),
            expect.anything(),
        );
    });

    it("restores the model selection when the conversion cannot be applied", () => {
        const models = catalog();
        const clip = minimalClip({
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
        const select = modelOptions(column)[0].parentElement;
        if (!(select instanceof HTMLSelectElement)) {
            throw new Error("missing model select");
        }

        select.value = "test-video.safetensors";
        select.dispatchEvent(new Event("change"));

        // This clip is not in the store, so the command cannot commit.
        expect(select.value).toBe("ltx");
    });

    it("uses authored WAN ownership instead of a stale cached LTX hint for an ordinary WAN retarget", () => {
        const models = catalogWithWan();
        const clip = minimalClip({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [
                minimalStage({
                    model: "wan-current.safetensors",
                    modelProfileId: "wan-i2v",
                }),
            ],
        });
        const persisted = mountClip(clip);
        const dispatch = dispatchSpy();
        const column = buildStageParamsColumn(
            context(models),
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

        select.value = "wan-alternate.safetensors";
        select.dispatchEvent(new Event("change"));

        expect(dispatch).toHaveBeenCalledWith(
            expect.objectContaining({ type: "stage.retarget-model" }),
            expect.anything(),
        );
    });

    it("uses authored WAN ownership instead of a stale cached LTX hint for whole-clip LTX conversion", () => {
        const models = catalogWithWan();
        const clip = minimalClip({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [
                minimalStage({
                    model: "wan-current.safetensors",
                    modelProfileId: "wan-i2v",
                }),
            ],
        });
        const persisted = mountClip(clip);
        const before = structuredClone(persisted);
        const dispatch = dispatchSpy();
        const column = buildStageParamsColumn(
            context(models),
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

        select.value = "ltx";
        select.dispatchEvent(new Event("change"));

        expect(dispatch).toHaveBeenCalledWith(
            expect.objectContaining({
                type: "clip.convert-architecture",
                target: expect.objectContaining({ architectureId: "ltx2" }),
            }),
            expect.anything(),
        );
        expect(persisted).toEqual(before);
    });

    it("commits a conversion through the UI as one exact undoable store change", async () => {
        const models = catalog();
        const dto = {
            schemaVersion: 2,
            architectures: structuredClone(models.architectures),
            models: models.entries.map((entry) => ({
                modelName: entry.value,
                architectureId: entry.architectureId as string,
                modelProfileId: entry.modelProfileId as string,
                modelClassId: entry.modelClassId as string,
                compatibilityClassId: entry.compatibilityClassId as string,
                frameGrid: entry.frameGrid ?? 1,
                frameGridOrigin: entry.frameGridOrigin ?? 1,
                capabilities: structuredClone(
                    entry.capabilities ??
                        models.architectures.find(
                            (architecture) =>
                                architecture.id === entry.architectureId,
                        )?.capabilities,
                ),
                enhancements: {
                    referencePositions:
                        entry.enhancements?.referencePositions ?? [],
                },
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
        history.rebase();
        getTimelineStore().subscribe((_state, meta) => {
            notifications.push(meta.origin);
            history.capture();
        });

        const before = JSON.stringify(getState());
        const revisionBefore = getTimelineStore().revision();
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

        expect(getTimelineStore().revision()).toBe(revisionBefore + 1);
        expect(notifications).toEqual(["detail-strip"]);
        // The timeline's store subscriber owns the render for this dispatch;
        // the model picker must not trigger a second paint itself.
        expect(panelContext.render).not.toHaveBeenCalled();
        expect(getState().clips[0]).toMatchObject({
            architectureHint: "test-video",
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

    it("enables sampler and scheduler controls for a resolved generating model", () => {
        const models = catalog();
        const stage = minimalStage({
            model: "test-video.safetensors",
            modelProfileId: "test-profile",
        });
        const clip = minimalClip({
            architectureHint: "test-video",
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

        expect(field("Sampler")?.querySelector("select")?.disabled).toBe(false);
        expect(field("Scheduler")?.querySelector("select")?.disabled).toBe(
            false,
        );
    });
});
