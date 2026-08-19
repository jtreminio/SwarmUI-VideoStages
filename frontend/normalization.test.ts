import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "./__test_helpers__/architectureFixtures";
import {
    canonicalizeIcLoraFields,
    defaultIcLora,
    hasSlotSourcedIcLora,
    normalizeIcLora,
    normalizeIcLoraDriveMediaKinds,
} from "./architectures/ltx2/icLoraNormalization";
import { clipReferenceTags } from "./clipReferenceAuthoring";
import {
    MEDIA_SOURCE_BASE,
    MEDIA_SOURCE_PREVIOUS_CLIP,
    MEDIA_SOURCE_REFINER,
    MEDIA_SOURCE_UPLOAD,
} from "./generatedMediaSource";
import {
    buildDefaultClip,
    normalizeClip,
    normalizeContinueOverlap,
} from "./normalizationClip";
import {
    normalizeClipReferences,
    normalizeInitVideo,
    normalizeRetake,
} from "./normalizationMedia";
import {
    appendRefToClip,
    buildDefaultRef,
    buildDefaultStage,
    normalizeRef,
    normalizeStage,
    normalizeStageLoras,
    normalizeStageRefStrengthValue,
    readRawStageProp,
    readRawStageString,
    removeRefAt,
} from "./normalizationStage";
import { framesForClip } from "./renderUtils";
import type { RootDefaults } from "./types";

const moduleRootDefaults = (): RootDefaults => ({
    modelCatalog: testArchitectureCatalog(),
    modelValues: ["ltx"],
    modelLabels: ["LTX"],
    loraValues: ["ltx-ic-lora.safetensors"],
    loraLabels: ["LTX IC LoRA"],
    loraDefaultWeights: [null],
    samplerValues: ["euler"],
    samplerLabels: ["Euler"],
    schedulerValues: ["normal"],
    schedulerLabels: ["Normal"],
    upscaleMethodValues: [
        "latentmodel-a.safetensors",
        "latentmodel-b.safetensors",
    ],
    upscaleMethodLabels: [
        "Latent Model: a.safetensors",
        "Latent Model: b.safetensors",
    ],
    width: 1024,
    height: 1024,
    fps: 24,
    frames: 48,
    control: 0.5,
    controlMin: 0,
    controlMax: 1,
    controlStep: 0.05,
    upscale: 1,
    upscaleMin: 0.25,
    upscaleMax: 4,
    upscaleStep: 0.25,
    steps: 8,
    stepsMin: 1,
    stepsMax: 50,
    stepsStep: 1,
    cfgScale: 1,
    cfgScaleMin: 0,
    cfgScaleMax: 10,
    cfgScaleStep: 0.5,
});

const testStageModel = moduleRootDefaults().modelValues[0] ?? "";

const minimalStageRaw = {
    model: "ltx",
    sampler: "euler",
    scheduler: "normal",
} as const;

describe("normalization", () => {
    it("readRawStageProp reads only canonical camelCase", () => {
        expect(
            readRawStageProp({ control: 0.5, Control: 0.9 }, "control"),
        ).toBe(0.5);
        expect(readRawStageProp({ Control: 0.9 }, "control")).toBeUndefined();
    });

    it("readRawStageString returns undefined for blank strings", () => {
        expect(
            readRawStageString({ upscaleMethod: "  " }, "upscaleMethod"),
        ).toBeUndefined();
    });

    it("normalizeRef clamps frame to max", () => {
        const ref = normalizeRef({ source: MEDIA_SOURCE_BASE, frame: 999 }, 10);
        expect(ref.frame).toBe(10);
    });

    it("normalizeClip preserves tail references while an authored model is unavailable", () => {
        const clip = normalizeClip(
            {
                duration: 1.05,
                stages: [{ ...minimalStageRaw, model: "removed-model" }],
                keyframes: [{ source: MEDIA_SOURCE_BASE, frame: 33 }],
            },
            moduleRootDefaults(),
            testStageModel,
        );

        expect(clip.stages[0].model).toBe("removed-model");
        expect(clip.frameRefs[0].frame).toBe(33);
    });

    it("normalizeStageRefStrengthValue accepts 0 without clamping up", () => {
        expect(normalizeStageRefStrengthValue(0)).toBe(0);
        expect(normalizeStageRefStrengthValue("0")).toBe(0);
        expect(normalizeStageRefStrengthValue(-0.5)).toBe(0);
    });

    it("normalizeClip defaults boundaryOut to cut when absent", () => {
        const clip = normalizeClip(
            { stages: [{ model: "ltx" }] },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.boundaryOut).toBe("cut");
        expect(clip.boundaryOutCarryAudio).toBe(false);
    });

    it("derives none for fresh source-only clips but preserves invalid v3 identity for diagnostics", () => {
        const initVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 2,
            startSeconds: 0,
            lengthSeconds: 2,
        };
        const fresh = normalizeClip(
            { initVideo, stages: [] },
            moduleRootDefaults(),
            testStageModel,
        );
        const invalid = normalizeClip(
            {
                architectureHint: "removed-architecture",
                modelProfileId: "removed-profile",
                initVideo,
                stages: [],
            },
            moduleRootDefaults(),
            testStageModel,
        );

        expect(fresh).toMatchObject({
            architectureHint: "none",
            modelProfileId: "none",
        });
        expect(invalid).toMatchObject({
            architectureHint: "removed-architecture",
            modelProfileId: "removed-profile",
        });
    });

    it("re-resolves a stale architecture hint when the model still resolves", () => {
        // A model can change hands between architectures between saves; the persisted hint is
        // repair information for an unresolvable model, not a pin.
        const clip = normalizeClip(
            {
                architectureHint: "host-video",
                modelProfileId: "host-video",
                stages: [{ model: "ltx", modelProfileId: "host-video" }],
            },
            moduleRootDefaults(),
            testStageModel,
        );

        expect(clip).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
        expect(clip.stages[0].modelProfileId).toBe("ltx-2.3");
    });

    it.each([
        ["continue", "continue"],
        ["crossfade", "crossfade"],
        ["Crossfade", "crossfade"],
        ["  CONTINUE ", "continue"],
        ["wipe", "cut"],
    ])("normalizeClip normalizes boundaryOut %s -> %s", (raw, expected) => {
        const clip = normalizeClip(
            { stages: [{ model: "ltx" }], boundaryOut: raw },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.boundaryOut).toBe(expected);
    });

    it.each([
        [undefined, "crop"],
        ["crop", "crop"],
        ["stretch", "stretch"],
        ["fit", "fit"],
        ["fit-green", "fit-green"],
        ["future", "crop"],
    ])("normalizeClip normalizes refFraming %s -> %s", (raw, expected) => {
        const clip = normalizeClip(
            { stages: [{ model: "ltx" }], refFraming: raw },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.refFraming).toBe(expected);
    });

    it.each([
        ["upload", "Upload"],
        ["CONTROLNET", "ControlNet"],
        ["  native  ", "Native"],
        ["", "Native"],
        // An indexed ref carries a track number, not a kind, so it keeps its spelling.
        ["audio3", "audio3"],
        ["future-source", "future-source"],
    ])("normalizeClip heals the audio source %p to the backend spelling %p", (raw, expected) => {
        const clip = normalizeClip(
            { stages: [{ model: "ltx" }], audioSource: raw },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.audioSource).toBe(expected);
    });

    it("normalizeClip ignores a noncanonical PascalCase boundary key", () => {
        const clip = normalizeClip(
            { stages: [{ model: "ltx" }], BoundaryOut: "continue" },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.boundaryOut).toBe("cut");
    });

    it("normalizeClip pads frameRefStrengths for each stage from raw", () => {
        const rawClip: Record<string, unknown> = {
            duration: 2,
            keyframes: [{ source: MEDIA_SOURCE_BASE, frame: 1 }],
            stages: [
                {
                    model: "ltx",
                    keyframeStrengths: [0.3],
                },
            ],
        };
        const clip = normalizeClip(
            rawClip,
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.frameRefs).toHaveLength(1);
        expect(clip.stages[0].frameRefStrengths).toEqual([0.3]);
    });

    it("normalizeClip clamps and defaults stage ControlNet strength", () => {
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx", controlNetStrength: 1.5 }, {}],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.stages[0].controlNetStrength).toBe(1);
        expect(clip.stages[1].controlNetStrength).toBe(1);

        const defaultClip = buildDefaultClip(
            moduleRootDefaults(),
            testStageModel,
        );
        expect(defaultClip.stages[0].controlNetStrength).toBe(0.8);

        const rawDefaultClip = normalizeClip(
            {
                stages: [{}],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(rawDefaultClip.stages[0].controlNetStrength).toBe(0.8);
    });

    it("buildDefaultClip copies base settings from a previous clip", () => {
        const prev = buildDefaultClip(moduleRootDefaults(), testStageModel);
        prev.duration = 3;
        prev.skipped = true;
        prev.boundaryOut = "continue";
        prev.boundaryOutOverlap = 16;
        prev.stages[0].sampler = "res_multistep";
        prev.stages[0].scheduler = "sgm_uniform";
        prev.stages[0].model = "ltx-big";
        prev.stages[0].steps = 20;
        prev.stages[0].cfgScale = 4;
        prev.h3TextEncoder = "4b";
        prev.loras = [{ name: "look", weight: 0.6 }];

        const clip = buildDefaultClip(
            moduleRootDefaults(),
            testStageModel,
            false,
            prev,
        );
        expect(clip.duration).toBe(3);
        expect(clip.skipped).toBe(true);
        const stage = clip.stages[0];
        expect(stage.sampler).toBe("res_multistep");
        expect(stage.scheduler).toBe("sgm_uniform");
        expect(stage.model).toBe("ltx-big");
        expect(stage.steps).toBe(20);
        expect(stage.cfgScale).toBe(4);
        expect(clip.h3TextEncoder).toBe("4b");
        expect(clip.loras).toEqual([{ name: "look", weight: 0.6 }]);
        expect(clip.loras).not.toBe(prev.loras);
        expect(clip.boundaryOut).toBe("cut");
        expect(clip.prompt).toBe("");
        expect(clip.frameRefs).toEqual([]);
    });

    it("normalizeClip ignores removed single-ControlNet IC-LoRA fields", () => {
        const defaultClip = normalizeClip(
            {},
            moduleRootDefaults(),
            testStageModel,
        );
        expect(defaultClip.icLoras).toEqual([]);

        const controlNetRaw = {
            ControlNetSource: "controlnet3",
            ControlNetLora: " ltx-ic-lora.safetensors ",
        };
        const controlNetClip = normalizeClip(
            controlNetRaw,
            moduleRootDefaults(),
            testStageModel,
        );
        expect(controlNetClip.icLoras).toEqual([]);
    });

    it("normalizeClip maps Swarm (None) legacy ControlNet LoRA token to no entries", () => {
        const clip = normalizeClip(
            { controlNetLora: " ( None ) " },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras).toEqual([]);
    });

    it("normalizeClip reads the icLoras array and clamps entry fields", () => {
        const clip = normalizeClip(
            {
                icLoras: [
                    {
                        lora: " detail-lora.safetensors ",
                        preset: "water-simulation",
                        strength: 99,
                        attentionStrength: -3,
                        controlType: "DEPTH",
                        driveMedia: {
                            data: "data:video/mp4;base64,QUJD",
                            fileName: "d.mp4",
                        },
                    },
                    { lora: "" },
                ],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras).toHaveLength(1);
        const entry = clip.icLoras[0];
        expect(entry.lora).toBe("detail-lora.safetensors");
        expect(entry.preset).toBe("water-simulation");
        expect(entry.driveSource).toBe("Upload");
        expect(entry.driveData).toBe("visual");
        expect(entry.strength).toBe(5);
        expect(entry.attentionStrength).toBe(0);
        expect(entry.controlType).toBe("depth");
        expect(entry.driveMedia).toEqual({
            data: "data:video/mp4;base64,QUJD",
            fileName: "d.mp4",
        });
    });

    it("preserves dormant LTX IC-LoRAs across a non-LTX save and reload", () => {
        const ltx = testArchitectureCatalog();
        const foreign = fakeArchitectureCatalog();
        const defaults = {
            ...moduleRootDefaults(),
            modelCatalog: {
                source: "backend" as const,
                architectures: [...ltx.architectures, ...foreign.architectures],
                entries: [...ltx.entries, ...foreign.entries],
            },
        };
        const raw = {
            architectureHint: "test-video",
            modelProfileId: "test-profile",
            icLoras: [
                {
                    lora: "guide.safetensors",
                    preset: "pose",
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMediaKinds: ["image", "video"],
                    stage: -1,
                    strength: 0.7,
                    attentionStrength: 0.8,
                    controlType: "depth",
                },
            ],
            stages: [
                {
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                },
            ],
        };

        const converted = normalizeClip(raw, defaults, "");
        const reloaded = normalizeClip(
            structuredClone(converted) as unknown as Record<string, unknown>,
            defaults,
            "",
        );
        const restoredRaw = structuredClone(reloaded);
        restoredRaw.architectureHint = "ltx2";
        restoredRaw.modelProfileId = "ltx-2.3";
        restoredRaw.stages[0].model = "ltx";
        restoredRaw.stages[0].modelProfileId = "ltx-2.3";
        const restored = normalizeClip(
            restoredRaw as unknown as Record<string, unknown>,
            defaults,
            "",
        );

        expect(converted.architectureHint).toBe("test-video");
        expect(converted.icLoras).toHaveLength(1);
        expect(reloaded.icLoras).toEqual(converted.icLoras);
        expect(reloaded.stages[0].icLoraStrengths).toEqual(
            converted.stages[0].icLoraStrengths,
        );
        expect(restored.icLoras).toEqual(converted.icLoras);
        expect(restored.stages[0].icLoraStrengths).toEqual(
            converted.stages[0].icLoraStrengths,
        );
    });

    it("normalizes audio Drive Media for LipDub", () => {
        const entry = normalizeIcLora({
            lora: "lipdub.safetensors",
            preset: "lipdub",
            driveData: "audio",
            driveMedia: {
                data: "data:audio/wav;base64,QUJD",
                fileName: "voice.wav",
            },
        });

        expect(entry?.driveMedia).toEqual({
            data: "data:audio/wav;base64,QUJD",
            fileName: "voice.wav",
        });
        expect(
            normalizeIcLora({
                lora: "lipdub.safetensors",
                preset: "lipdub",
                driveData: "audio",
                driveMedia: {
                    data: "data:image/png;base64,QUJD",
                    fileName: "not-a-speaker.png",
                },
            })?.driveMedia,
        ).toBeNull();
    });

    it("preserves a normalized IC-LoRA identity", () => {
        expect(
            normalizeIcLora({
                id: "  ic-guide  ",
                lora: "guide.safetensors",
            })?.id,
        ).toBe("ic-guide");
    });

    it("repairs legacy Custom + [AUTO] entries to the stable default preset", () => {
        expect(
            normalizeIcLora({
                lora: "[AUTO]",
                preset: "custom",
                strength: 2,
                controlType: "none",
            }),
        ).toMatchObject({
            lora: "[AUTO]",
            preset: "union-control",
            strength: 1,
            controlType: "depth",
        });
    });

    it.each([
        "Stage Input",
        "stage input",
        "STAGEINPUT",
    ])("heals the pre-rename drive source %p to Incoming", (driveSource) => {
        expect(
            normalizeIcLora({ lora: "a.safetensors", driveSource })
                ?.driveSource,
        ).toBe("Incoming");
    });

    // The backend's Compact drops spaces and nothing else, so a tab-bearing spelling stays
    // unrecognized there. This is the case that separates the two compacting rules.
    it("leaves a tab inside the legacy drive source alone, as the backend does", () => {
        expect(
            normalizeIcLora({
                lora: "a.safetensors",
                driveSource: "Stage\tInput",
            })?.driveSource,
        ).toBe("Stage\tInput");
    });

    it("preserves explicit LipDub audio data and Incoming source", () => {
        expect(
            normalizeIcLora(
                {
                    lora: "lipdub.safetensors",
                    preset: "lipdub",
                    driveData: "audio",
                    driveSource: "Incoming",
                    controlType: "canny",
                },
                2,
                true,
            )?.driveSource,
        ).toBe("Incoming");
        expect(
            normalizeIcLora({
                lora: "lipdub.safetensors",
                preset: "lipdub",
                driveData: "audio",
                controlType: "canny",
            }),
        ).toMatchObject({ controlType: "none", driveData: "audio" });
    });

    it("does not let a recognized preset override its persisted DriveData", () => {
        expect(
            normalizeIcLora({
                lora: "lipdub.safetensors",
                preset: "lipdub",
                driveData: "visual",
            }),
        ).toMatchObject({ driveData: "visual" });
    });

    it("preserves an explicit image-only visual contract independently of its preset", () => {
        const entry = normalizeIcLora({
            lora: "lipdub.safetensors",
            preset: "lipdub",
            driveData: "visual",
            driveMediaKinds: ["image"],
            driveMedia: {
                data: "data:image/png;base64,QUJD",
                fileName: "guide.png",
            },
        });

        expect(entry).toMatchObject({
            driveData: "visual",
            driveMediaKinds: ["image"],
        });
        expect(entry?.driveMedia).not.toBeNull();
        expect(
            normalizeIcLora({
                lora: "guide.safetensors",
                driveData: "visual",
                driveMediaKinds: ["image"],
                driveMedia: {
                    data: "data:video/mp4;base64,QUJD",
                    fileName: "not-accepted.mp4",
                },
            })?.driveMedia,
        ).toBeNull();
        expect(
            normalizeIcLoraDriveMediaKinds(
                ["image", "audio", "image"],
                "visual",
            ),
        ).toEqual(["image"]);
    });

    it("normalizeClip reads the IC-LoRA stage and Incoming source", () => {
        const clip = normalizeClip(
            {
                icLoras: [
                    { lora: "a", stage: 1, driveSource: "Incoming" },
                    { lora: "b", stage: 2.7 },
                    { lora: "c", stage: -5 },
                    { lora: "d", stage: null },
                ],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras.map((e) => e.stage)).toEqual([1, 2, -1, -1]);
        expect(clip.icLoras[0].driveSource).toBe("Incoming");
        expect(clip.icLoras[1].driveSource).toBe("Upload");
        // Incoming is not a captured "ControlNet N" slot source.
        expect(hasSlotSourcedIcLora(clip.icLoras)).toBe(false);
    });

    it("normalizeClip preserves Incoming for availability diagnostics", () => {
        const clip = normalizeClip(
            { icLoras: [{ lora: "a", driveSource: "Incoming" }] },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras[0].driveSource).toBe("Incoming");
        expect(clip.icLoras[0].stage).toBe(-1);
    });

    it("normalizeClip keeps Control 0 (passthrough) on init-video and refine stages", () => {
        // Control 0 means passthrough; init-video and refine stages may both skip sampling.
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx", control: 0 }, { control: 0 }],
                initVideo: {
                    data: "data:video/mp4;base64,QUJD",
                    lengthSeconds: 3,
                },
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.stages[0].control).toBe(0);
        expect(clip.stages[1].control).toBe(0);
    });

    it("normalizeClip keeps an Incoming source at stage 0 on a init-video clip", () => {
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx" }],
                initVideo: {
                    data: "data:video/mp4;base64,QUJD",
                    lengthSeconds: 3,
                },
                icLoras: [{ lora: "a", stage: 0, driveSource: "Incoming" }],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        // Stage 0 receives the init-video footage as its incoming frames.
        expect(clip.initVideo).not.toBeNull();
        expect(clip.icLoras[0].driveSource).toBe("Incoming");
        expect(clip.icLoras[0].stage).toBe(0);
    });

    it("normalizeClip does not silently rewrite unavailable Incoming media", () => {
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx" }],
                icLoras: [{ lora: "a", stage: 0, driveSource: "Incoming" }],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras[0].driveSource).toBe("Incoming");
    });

    it("normalizeClip preserves an unknown IC-LoRA source for fail-closed diagnostics", () => {
        const clip = normalizeClip(
            {
                icLoras: [
                    {
                        lora: "drive.safetensors",
                        driveSource: "  future-slot  ",
                    },
                ],
            },
            moduleRootDefaults(),
            testStageModel,
        );

        expect(clip.icLoras[0].driveSource).toBe("future-slot");
        expect(hasSlotSourcedIcLora(clip.icLoras)).toBe(false);
    });

    it("normalizeIcLora keeps Incoming independent of clip placement", () => {
        const raw = { lora: "a", stage: 0, driveSource: "Incoming" };
        expect(normalizeIcLora(raw, 0, false)?.driveSource).toBe("Incoming");
        expect(normalizeIcLora(raw, 0, true)?.driveSource).toBe("Incoming");
    });

    it("canonicalizeIcLoraFields clears hidden model-only media", () => {
        const modelOnly = defaultIcLora({
            driveSource: "Incoming",
            driveData: "none",
            driveMedia: {
                data: "data:video/mp4;base64,QUJD",
                fileName: "stale.mp4",
            },
        });
        canonicalizeIcLoraFields(modelOnly);
        expect(modelOnly.driveSource).toBe("Upload");
        expect(modelOnly.driveMedia).toBeNull();
    });

    it("normalizeClip heals an IC-LoRA stage target beyond the clip's stage list", () => {
        const clip = normalizeClip(
            {
                icLoras: [
                    { lora: "a", stage: 2 },
                    { lora: "b", stage: 1, driveSource: "Incoming" },
                    { lora: "c", stage: 0 },
                ],
                stages: [{}],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras.map((e) => e.stage)).toEqual([-1, -1, 0]);
        expect(clip.icLoras[1].driveSource).toBe("Incoming");
    });

    it("normalizeClip prefers the icLoras array over legacy fields", () => {
        const clip = normalizeClip(
            {
                icLoras: [{ lora: "new-lora" }],
                controlNetLora: "old-lora",
                controlNetSource: "ControlNet 2",
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras).toHaveLength(1);
        expect(clip.icLoras[0].lora).toBe("new-lora");
        expect(clip.icLoras[0].driveSource).toBe("Upload");
    });

    it("normalizeClip lets stored ControlNet length override audio length", () => {
        const clip = normalizeClip(
            {
                audioSource: "Upload",
                controlNetLora: "ltx-ic-lora.safetensors",
                clipLengthFromAudio: true,
                clipLengthFromControlNet: true,
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.clipLengthFromAudio).toBe(false);
        expect(clip.clipLengthFromControlNet).toBe(true);
    });

    it("normalizeClip preserves a length flag its current source cannot honor", () => {
        const clip = normalizeClip(
            {
                audioSource: "Native",
                clipLengthFromAudio: true,
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.clipLengthFromAudio).toBe(true);
    });

    it("normalizeClip preserves reference positions while clip length is runtime-derived", () => {
        const clip = normalizeClip(
            {
                duration: 1.05,
                audioSource: "Upload",
                clipLengthFromAudio: true,
                keyframes: [{ source: "Base", frame: 100 }],
                stages: [{ model: "ltx-2.3.safetensors" }],
            },
            moduleRootDefaults(),
            testStageModel,
            24,
        );

        expect(clip.frameRefs[0].frame).toBe(100);
        expect(clip.clipLengthFromAudio).toBe(true);
    });

    it("normalizeClip clamps references when the audio source cannot drive the length", () => {
        const clip = normalizeClip(
            {
                duration: 1.05,
                audioSource: "Native",
                clipLengthFromAudio: true,
                keyframes: [{ source: "Base", frame: 100 }],
                stages: [{ model: "ltx-2.3.safetensors" }],
            },
            moduleRootDefaults(),
            testStageModel,
            24,
        );

        // The inert flag no longer suppresses grid snapping, so references clamp to the
        // clip's grid-snapped frame count exactly as they would without the flag.
        expect(clip.frameRefs[0].frame).toBe(
            framesForClip(clip.duration, 24, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        );
        expect(clip.clipLengthFromAudio).toBe(true);
    });

    it("normalizeClip preserves ControlNet length without a slot-init-video IC-LoRA", () => {
        const clip = normalizeClip(
            {
                audioSource: "Native",
                clipLengthFromControlNet: true,
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras).toEqual([]);
        expect(clip.clipLengthFromControlNet).toBe(true);
    });

    it("normalizeClip keeps ControlNet precedence without an IC-LoRA source", () => {
        const clip = normalizeClip(
            {
                audioSource: "Upload",
                controlNetLora: "(None)",
                clipLengthFromAudio: true,
                clipLengthFromControlNet: true,
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras).toEqual([]);
        expect(clip.clipLengthFromAudio).toBe(false);
        expect(clip.clipLengthFromControlNet).toBe(true);
    });

    it("normalizeClip preserves 'ControlNet' audio source when controlNetLora is set", () => {
        const clip = normalizeClip(
            {
                audioSource: "ControlNet",
                controlNetLora: "ltx-ic-lora.safetensors",
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.audioSource).toBe("ControlNet");
    });

    it("normalizeClip preserves an unsupported persisted ControlNet audio source", () => {
        const clip = normalizeClip(
            {
                audioSource: "ControlNet",
                controlNetLora: "",
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.audioSource).toBe("ControlNet");
    });

    it("normalizeClip allows clipLengthFromAudio when audio source is ControlNet", () => {
        const clip = normalizeClip(
            {
                audioSource: "ControlNet",
                controlNetLora: "ltx-ic-lora.safetensors",
                clipLengthFromAudio: true,
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.clipLengthFromAudio).toBe(true);
    });

    it("normalizeClip ignores removed camelCase single-ControlNet fields", () => {
        const clip = normalizeClip(
            {
                controlNetSource: "ControlNet 2",
                controlNetLora: " detail-lora.safetensors ",
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.icLoras).toEqual([]);
    });

    it("normalizeStage reads canonical upscale fields for non-first stage", () => {
        const stage0 = normalizeStage(
            moduleRootDefaults(),
            testStageModel,
            { ...minimalStageRaw },
            null,
            0,
            0,
        );
        const stage1 = normalizeStage(
            moduleRootDefaults(),
            testStageModel,
            {
                ...minimalStageRaw,
                upscale: 2,
                upscaleMethod: "latentmodel-b.safetensors",
            },
            stage0,
            0,
            1,
        );
        expect(stage1.upscale).toBe(2);
        expect(stage1.upscaleMethod).toBe("latentmodel-b.safetensors");
    });

    it("normalizeStage snaps upscale to the 0.25 step", () => {
        const stage0 = normalizeStage(
            moduleRootDefaults(),
            testStageModel,
            { ...minimalStageRaw },
            null,
            0,
            0,
        );
        const snap = (upscale: number): number =>
            normalizeStage(
                moduleRootDefaults(),
                testStageModel,
                { ...minimalStageRaw, upscale },
                stage0,
                0,
                1,
            ).upscale;
        expect(snap(1.3)).toBe(1.25);
        expect(snap(1.1)).toBe(1);
        expect(snap(0.3)).toBe(0.25);
    });

    it("normalizeStage forces first-stage control to the root default", () => {
        const stage0 = normalizeStage(
            moduleRootDefaults(),
            testStageModel,
            {
                ...minimalStageRaw,
                control: 0.4,
            },
            null,
            0,
            0,
        );

        expect(stage0.control).toBe(0.5);
    });

    it("normalizeStage keeps authored control/upscale on a init-video clip stage 0", () => {
        const stage0 = normalizeStage(
            moduleRootDefaults(),
            testStageModel,
            {
                ...minimalStageRaw,
                control: 0.4,
                upscale: 1.3,
                upscaleMethod: "latentmodel-b.safetensors",
            },
            null,
            0,
            0,
            true,
        );

        // Init-video stage 0 is an img2img refinement, so authored settings remain active.
        expect(stage0.control).toBe(0.4);
        expect(stage0.upscale).toBe(1.25);
        expect(stage0.upscaleMethod).toBe("latentmodel-b.safetensors");
    });

    it("normalizeClip keeps authored stage-0 refine params for a init-video clip", () => {
        const clip = normalizeClip(
            {
                stages: [
                    {
                        model: "ltx",
                        control: 0.3,
                        upscale: 2,
                        upscaleMethod: "latentmodel-b.safetensors",
                    },
                ],
                initVideo: {
                    data: "data:video/mp4;base64,QUJD",
                    lengthSeconds: 3,
                },
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.initVideo).not.toBeNull();
        expect(clip.stages[0].control).toBe(0.3);
        expect(clip.stages[0].upscale).toBe(2);
        expect(clip.stages[0].upscaleMethod).toBe("latentmodel-b.safetensors");
    });

    it("normalizeStage reads canonical control for non-first stage", () => {
        const stage0 = normalizeStage(
            moduleRootDefaults(),
            testStageModel,
            { ...minimalStageRaw },
            null,
            0,
            0,
        );
        const stage1 = normalizeStage(
            moduleRootDefaults(),
            testStageModel,
            {
                ...minimalStageRaw,
                control: 0.4,
            },
            stage0,
            0,
            1,
        );

        expect(stage1.control).toBe(0.4);
    });

    it("buildDefaultRef matches editor defaults", () => {
        const ref = buildDefaultRef();
        expect(ref.source).toBe(MEDIA_SOURCE_REFINER);
        expect(ref.frame).toBe(1);
        expect(ref.uploadedImage).toBeNull();
    });
});

describe("normalizeContinueOverlap", () => {
    it.each([
        [undefined, 1],
        [null, 1],
        [Number.NaN, 1],
        ["garbage", 1],
        [7, 7],
        [16, 16],
        [20, 20],
        [48, 48],
        [100, 100],
        ["24", 24],
    ])("normalizes %s -> %s", (raw, expected) => {
        expect(normalizeContinueOverlap(raw)).toBe(expected);
    });
});

describe("appendRefToClip / removeRefAt", () => {
    const twoStageClip = () =>
        normalizeClip(
            {
                duration: 4,
                keyframes: [{ source: MEDIA_SOURCE_REFINER, frame: 2 }],
                stages: [
                    { keyframeStrengths: [0.3] },
                    { keyframeStrengths: [0.7] },
                ],
            },
            moduleRootDefaults(),
            testStageModel,
        );

    it("appendRefToClip adds the ref and pads every stage's frameRefStrengths", () => {
        const clip = twoStageClip();
        appendRefToClip(clip, buildDefaultRef(MEDIA_SOURCE_BASE));
        expect(clip.frameRefs).toHaveLength(2);
        expect(clip.frameRefs[1].source).toBe(MEDIA_SOURCE_BASE);
        expect(clip.stages[0].frameRefStrengths).toHaveLength(2);
        expect(clip.stages[1].frameRefStrengths).toHaveLength(2);
        expect(clip.stages[0].frameRefStrengths[0]).toBe(0.3);
        expect(clip.stages[1].frameRefStrengths[0]).toBe(0.7);
        expect(clip.stages[0].frameRefStrengths[1]).toBe(0.8);
    });

    it("removeRefAt removes the ref and the matching strength from every stage", () => {
        const clip = twoStageClip();
        appendRefToClip(clip, buildDefaultRef(MEDIA_SOURCE_BASE));
        expect(removeRefAt(clip, 0)).toBe(true);
        expect(clip.frameRefs).toHaveLength(1);
        expect(clip.frameRefs[0].source).toBe(MEDIA_SOURCE_BASE);
        expect(clip.stages[0].frameRefStrengths).toEqual([0.8]);
        expect(clip.stages[1].frameRefStrengths).toEqual([0.8]);
    });

    it("removeRefAt returns false and leaves the clip untouched for an out-of-range index", () => {
        const clip = twoStageClip();
        expect(removeRefAt(clip, 5)).toBe(false);
        expect(removeRefAt(clip, -1)).toBe(false);
        expect(clip.frameRefs).toHaveLength(1);
        expect(clip.stages[0].frameRefStrengths).toHaveLength(1);
    });
});

describe("clip LoRAs with per-stage weights", () => {
    it("normalizeStageLoras parses tolerant entries and drops invalid ones", () => {
        expect(
            normalizeStageLoras([
                { name: "a.safetensors", weight: 0.5 },
                { name: "b.safetensors", weight: "1.25" },
                { name: "  ", weight: 1 },
                { name: "c.safetensors" },
                { weight: 2 },
                "nope",
            ]),
        ).toEqual([
            { name: "a.safetensors", weight: 0.5 },
            { name: "b.safetensors", weight: 1.25 },
            { name: "c.safetensors", weight: 1 },
        ]);
        expect(normalizeStageLoras(undefined)).toEqual([]);
        expect(normalizeStageLoras("x")).toEqual([]);
    });

    it("buildDefaultStage prefers pixel Lanczos over latent-model upscalers", () => {
        const defaults = moduleRootDefaults();
        defaults.upscaleMethodValues = [
            "latentmodel-a.safetensors",
            "pixel-lanczos",
        ];

        const stage = buildDefaultStage(defaults, testStageModel, null, 0);

        expect(stage.upscaleMethod).toBe("pixel-lanczos");
    });

    it("normalizeClip round-trips loras across a multi-stage clip", () => {
        const clip = normalizeClip(
            {
                duration: 2,
                keyframes: [],
                stages: [
                    {
                        model: "ltx",
                        loras: [{ name: "base.safetensors", weight: 1 }],
                    },
                    {
                        model: "ltx",
                        loras: [{ name: "refine.safetensors", weight: 0.4 }],
                    },
                ],
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.loras).toEqual([
            { name: "base.safetensors", weight: 1 },
            { name: "refine.safetensors", weight: 0.4 },
        ]);
        expect(clip.stages[0]).not.toHaveProperty("loraWeights");
        expect(clip.stages[1]).not.toHaveProperty("loraWeights");
    });

    describe("normalizeRetake", () => {
        it("keeps a valid window and defaults strength to 1", () => {
            expect(
                normalizeRetake({ startSeconds: 1, lengthSeconds: 2 }, 10),
            ).toEqual({ startSeconds: 1, lengthSeconds: 2, strength: 1 });
        });

        it("reads canonical keys and clamps strength to [0, 1]", () => {
            expect(
                normalizeRetake(
                    { startSeconds: 0, lengthSeconds: 1, strength: 5 },
                    10,
                ),
            ).toEqual({ startSeconds: 0, lengthSeconds: 1, strength: 1 });
            expect(
                normalizeRetake(
                    { startSeconds: 0, lengthSeconds: 1, strength: -3 },
                    10,
                )?.strength,
            ).toBe(0);
        });

        it("clamps length so the window fits inside the clip", () => {
            expect(
                normalizeRetake({ startSeconds: 8, lengthSeconds: 5 }, 10),
            ).toEqual({ startSeconds: 8, lengthSeconds: 2, strength: 1 });
        });

        it("clamps an over-far start to leave room for the minimum window", () => {
            const r = normalizeRetake(
                { startSeconds: 100, lengthSeconds: 2 },
                10,
            );
            expect(r?.startSeconds).toBeCloseTo(9.9, 5);
            expect(r?.lengthSeconds).toBeCloseTo(0.1, 5);
        });

        it("returns null for absent, non-object, or non-positive length", () => {
            expect(normalizeRetake(undefined, 10)).toBeNull();
            expect(normalizeRetake(null, 10)).toBeNull();
            expect(normalizeRetake("nope", 10)).toBeNull();
            expect(
                normalizeRetake({ startSeconds: 1, lengthSeconds: 0 }, 10),
            ).toBeNull();
            expect(
                normalizeRetake({ startSeconds: 1, lengthSeconds: -2 }, 10),
            ).toBeNull();
        });
    });

    it("normalizeClip carries an embedded retake and defaults to null when absent", () => {
        const withRetake = normalizeClip(
            {
                duration: 5,
                stages: [minimalStageRaw],
                retake: { startSeconds: 1, lengthSeconds: 2, strength: 0.5 },
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(withRetake.retake).toEqual({
            startSeconds: 1,
            lengthSeconds: 2,
            strength: 0.5,
        });

        const withoutRetake = normalizeClip(
            { duration: 5, stages: [minimalStageRaw] },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(withoutRetake.retake).toBeNull();
    });
});

describe("normalizeInitVideo", () => {
    const data = "data:video/mp4;base64,QUJD";

    it("keeps a valid source video and rounds its seconds", () => {
        expect(
            normalizeInitVideo({
                data,
                fileName: "shot.mp4",
                fps: 24,
                durationSeconds: 10.04,
                startSeconds: 2.04,
                lengthSeconds: 5.06,
            }),
        ).toEqual({
            source: MEDIA_SOURCE_UPLOAD,
            data,
            fileName: "shot.mp4",
            fps: 24,
            durationSeconds: 10,
            startSeconds: 2,
            lengthSeconds: 5.1,
        });
    });

    it("rejects a missing data blob or non-record value", () => {
        expect(normalizeInitVideo(null)).toBeNull();
        expect(normalizeInitVideo("x")).toBeNull();
        expect(normalizeInitVideo({ data: " ", lengthSeconds: 2 })).toBeNull();
    });

    it("keeps a previous-clip source without an upload", () => {
        expect(
            normalizeInitVideo({
                source: MEDIA_SOURCE_PREVIOUS_CLIP,
                durationSeconds: 3.2,
                lengthSeconds: 3.2,
            }),
        ).toEqual({
            source: MEDIA_SOURCE_PREVIOUS_CLIP,
            data: "",
            fileName: null,
            fps: 0,
            durationSeconds: 3.2,
            startSeconds: 0,
            lengthSeconds: 3.2,
        });
    });

    it("clamps the range inside a known file duration", () => {
        expect(
            normalizeInitVideo({
                data,
                fps: 0,
                durationSeconds: 6,
                startSeconds: 9,
                lengthSeconds: 4,
            }),
        ).toEqual({
            source: MEDIA_SOURCE_UPLOAD,
            data,
            fileName: null,
            fps: 0,
            durationSeconds: 6,
            startSeconds: 5,
            lengthSeconds: 1,
        });
    });

    it("defaults a missing length to the rest of the file", () => {
        expect(
            normalizeInitVideo({
                data,
                durationSeconds: 8,
                startSeconds: 3,
            }),
        ).toEqual({
            source: MEDIA_SOURCE_UPLOAD,
            data,
            fileName: null,
            fps: 0,
            durationSeconds: 8,
            startSeconds: 3,
            lengthSeconds: 5,
        });
    });

    it("keeps a positive length even when the file duration is unknown", () => {
        expect(
            normalizeInitVideo({ data, lengthSeconds: 3 })?.lengthSeconds,
        ).toBe(3);
        expect(normalizeInitVideo({ data })).toBeNull();
    });

    it("drives the clip duration from the source range in normalizeClip", () => {
        const clip = normalizeClip(
            {
                duration: 2,
                stages: [minimalStageRaw],
                initVideo: {
                    data,
                    durationSeconds: 10,
                    startSeconds: 1,
                    lengthSeconds: 3.5,
                },
            },
            moduleRootDefaults(),
            testStageModel,
        );
        expect(clip.initVideo?.startSeconds).toBe(1);
        expect(clip.duration).toBe(3.5);
    });
});

describe("normalizeClipReferences", () => {
    it("keeps the authored order and per-kind fields", () => {
        expect(
            normalizeClipReferences([
                {
                    kind: "IMAGE",
                    source: "Base",
                    uploadedMedia: null,
                    includeSoundtrack: true,
                },
                {
                    kind: "video",
                    source: "Refiner",
                    uploadedMedia: {
                        data: "data:video/mp4;base64,REVG",
                        fileName: "motion.mp4",
                    },
                    includeSoundtrack: true,
                },
            ]),
        ).toEqual([
            {
                id: undefined,
                kind: "image",
                source: "Base",
                uploadedMedia: null,
                // Only a video reference has a soundtrack to include.
                includeSoundtrack: false,
                mediaDurationSeconds: 0,
                drivesClipLength: false,
                mediaScale: 1,
                startSeconds: 0,
                lengthSeconds: 0,
            },
            {
                id: undefined,
                kind: "video",
                source: "Refiner",
                uploadedMedia: {
                    data: "data:video/mp4;base64,REVG",
                    fileName: "motion.mp4",
                },
                includeSoundtrack: true,
                mediaDurationSeconds: 0,
                drivesClipLength: false,
                mediaScale: 1,
                startSeconds: 0,
                lengthSeconds: 0,
            },
        ]);
    });

    it("keeps a video reference's trim inside its probed length", () => {
        const [reference] = normalizeClipReferences([
            {
                kind: "video",
                source: "Upload",
                mediaDurationSeconds: 8,
                startSeconds: 2,
                lengthSeconds: 99,
            },
        ]);
        expect(reference).toMatchObject({ startSeconds: 2, lengthSeconds: 6 });
    });

    it("keeps an audio reference's trim inside its probed length", () => {
        const [reference] = normalizeClipReferences([
            {
                kind: "audio",
                source: "Upload",
                mediaDurationSeconds: 8,
                startSeconds: 2,
                lengthSeconds: 99,
            },
        ]);
        expect(reference).toMatchObject({ startSeconds: 2, lengthSeconds: 6 });
    });

    /**
     * The backend pads a short window by freezing the last frame, so it cannot
     * express "from here to the end". A start with no length is untrimmed.
     */
    it("drops a start that carries no length behind it", () => {
        const [reference] = normalizeClipReferences([
            {
                kind: "video",
                source: "Upload",
                mediaDurationSeconds: 8,
                startSeconds: 3,
            },
        ]);
        expect(reference).toMatchObject({ startSeconds: 0, lengthSeconds: 0 });
    });

    it("ignores a trim on a kind that has no timebase", () => {
        const [reference] = normalizeClipReferences([
            {
                kind: "image",
                source: "Upload",
                mediaDurationSeconds: 8,
                startSeconds: 1,
                lengthSeconds: 2,
            },
        ]);
        expect(reference).toMatchObject({ startSeconds: 0, lengthSeconds: 0 });
    });

    it("ignores a trim with no probed length to clamp against", () => {
        const [reference] = normalizeClipReferences([
            {
                kind: "video",
                source: "Upload",
                mediaDurationSeconds: 0,
                startSeconds: 1,
                lengthSeconds: 2,
            },
        ]);
        expect(reference).toMatchObject({ startSeconds: 0, lengthSeconds: 0 });
    });

    it("preserves ControlNet sources on every kind and AceStepFun on audio", () => {
        expect(
            normalizeClipReferences([
                { kind: "image", source: "ControlNet 1" },
                { kind: "video", source: "ControlNet 2" },
                { kind: "audio", source: "ControlNet 3" },
                { kind: "audio", source: "audio0" },
            ]).map((reference) => reference.source),
        ).toEqual(["ControlNet 1", "ControlNet 2", "ControlNet 3", "audio0"]);
    });

    it("keeps an unusable entry rather than silently dropping a prompt tag", () => {
        const references = normalizeClipReferences([{}, "nonsense"]);

        expect(references).toHaveLength(2);
        expect(references.every((entry) => entry.kind === "image")).toBe(true);
        expect(references.every((entry) => entry.uploadedMedia === null)).toBe(
            true,
        );
    });

    it("returns an empty list for a document that has no references", () => {
        expect(normalizeClipReferences(undefined)).toEqual([]);
        expect(
            normalizeClip({ stages: [] }, moduleRootDefaults(), testStageModel)
                .references,
        ).toEqual([]);
    });
});

describe("clipReferenceTags", () => {
    it("numbers each kind independently, in authored order", () => {
        expect(
            clipReferenceTags([
                { kind: "video", includeSoundtrack: false },
                { kind: "image", includeSoundtrack: false },
                { kind: "video", includeSoundtrack: false },
                { kind: "audio", includeSoundtrack: false },
                { kind: "image", includeSoundtrack: false },
            ]),
        ).toEqual([
            "<Video 1>",
            "<Picture 1>",
            "<Video 2>",
            "<Audio 1>",
            "<Picture 2>",
        ]);
    });

    // The node presents an included soundtrack as its own audio item, ahead of
    // every standalone audio reference, so it consumes an audio ordinal.
    it("counts an included soundtrack against the audio ordinals", () => {
        expect(
            clipReferenceTags([
                { kind: "audio", includeSoundtrack: false },
                { kind: "video", includeSoundtrack: true },
                { kind: "audio", includeSoundtrack: false },
                { kind: "video", includeSoundtrack: false },
            ]),
        ).toEqual(["<Audio 2>", "<Video 1>", "<Audio 3>", "<Video 2>"]);
    });
});

describe("clip reference media scale", () => {
    const scaleOf = (raw: Record<string, unknown>) =>
        normalizeClipReferences([{ kind: "video", ...raw }])[0].mediaScale;

    it.each([
        [0.5, 0.5],
        [0.25, 0.25],
        [1, 1],
        // Outside the offered factors, so it falls back rather than resampling
        // to a size nobody picked.
        [0.7, 1],
        ["half", 1],
        [undefined, 1],
    ])("normalizes an authored %p to %p", (authored, expected) => {
        expect(scaleOf({ mediaScale: authored })).toBe(expected);
    });

    it("keeps only a video reference scalable", () => {
        expect(
            normalizeClipReferences([{ kind: "audio", mediaScale: 0.5 }])[0]
                .mediaScale,
        ).toBe(1);
    });
});

describe("clip length from a reference", () => {
    const videoReference = (
        overrides: Record<string, unknown> = {},
    ): Record<string, unknown> => ({
        kind: "video",
        source: "Upload",
        uploadedMedia: {
            data: "data:video/mp4;base64,REVG",
            fileName: "m.mp4",
        },
        mediaDurationSeconds: 4.5,
        drivesClipLength: true,
        ...overrides,
    });

    const clipWith = (
        references: Record<string, unknown>[],
        rest: Record<string, unknown> = {},
    ) =>
        normalizeClip(
            { duration: 2, stages: [], references, ...rest },
            moduleRootDefaults(),
            testStageModel,
            24,
        );

    it("takes the clip duration from the reference's probed length", () => {
        expect(clipWith([videoReference()]).duration).toBe(4.5);
    });

    it("keeps only the first claim so one source owns the length", () => {
        const clip = clipWith([
            videoReference({ mediaDurationSeconds: 4.5 }),
            videoReference({ mediaDurationSeconds: 9 }),
        ]);

        expect(clip.references.map((r) => r.drivesClipLength)).toEqual([
            true,
            false,
        ]);
        expect(clip.duration).toBe(4.5);
    });

    it("refuses the claim on an image reference, which has no length", () => {
        const clip = clipWith([
            videoReference({ kind: "image", uploadedMedia: null }),
        ]);

        expect(clip.references[0].drivesClipLength).toBe(false);
        expect(clip.duration).toBe(2);
    });

    it("ignores a claim with no probed length", () => {
        expect(
            clipWith([videoReference({ mediaDurationSeconds: 0 })]).duration,
        ).toBe(2);
    });

    /**
     * The whole point of trimming a driving reference is to use part of it. If
     * the clip still took the file's full length, a 3 s trim of a 30 s motion
     * reference would leave a 30 s clip conditioned on 3 s of footage.
     */
    it("takes the trimmed length, not the file's, from a driving reference", () => {
        expect(
            clipWith([videoReference({ startSeconds: 1, lengthSeconds: 2.5 })])
                .duration,
        ).toBe(2.5);
    });

    it("yields to the clip-level length flags", () => {
        const fromAudio = clipWith([videoReference()], {
            clipLengthFromAudio: true,
        });
        const fromControlNet = clipWith([videoReference()], {
            clipLengthFromControlNet: true,
        });

        expect(fromAudio.references[0].drivesClipLength).toBe(false);
        expect(fromAudio.duration).toBe(2);
        expect(fromControlNet.references[0].drivesClipLength).toBe(false);
    });

    it("yields to an init video, whose range is the clip", () => {
        const clip = clipWith([videoReference()], {
            initVideo: {
                data: "data:video/mp4;base64,QQ==",
                durationSeconds: 10,
                startSeconds: 0,
                lengthSeconds: 3,
            },
        });

        expect(clip.duration).toBe(3);
        // The authored claim survives for repair rather than being erased.
        expect(clip.references[0].drivesClipLength).toBe(true);
    });
});
