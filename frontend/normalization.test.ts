import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import {
    canonicalizeIcLoraFields,
    defaultIcLora,
    hasSlotSourcedIcLora,
    normalizeIcLora,
    normalizeIcLoraDriveMediaKinds,
} from "./architectures/ltx2/icLoraNormalization";
import {
    appendRefToClip,
    buildDefaultClip,
    buildDefaultRef,
    buildDefaultStage,
    normalizeClip,
    normalizeContinueOverlap,
    normalizeRef,
    normalizeRetake,
    normalizeSourceVideo,
    normalizeStage,
    normalizeStageLoras,
    normalizeStageRefStrengthValue,
    readRawStageProp,
    readRawStageString,
    removeRefAt,
} from "./normalization";
import {
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    type RootDefaults,
} from "./types";

const getRootDefaults = (): RootDefaults => ({
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

const getDefaultStageModel = (modelValues: string[]): string =>
    modelValues[0] ?? "";

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
        const ref = normalizeRef({ source: REF_SOURCE_BASE, frame: 999 }, 10);
        expect(ref.frame).toBe(10);
    });

    it("normalizeStageRefStrengthValue accepts 0 without clamping up", () => {
        expect(normalizeStageRefStrengthValue(0)).toBe(0);
        expect(normalizeStageRefStrengthValue("0")).toBe(0);
        expect(normalizeStageRefStrengthValue(-0.5)).toBe(0);
    });

    it("normalizeClip defaults boundaryOut to cut when absent", () => {
        const clip = normalizeClip(
            { stages: [{ model: "ltx" }] },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.boundaryOut).toBe("cut");
        expect(clip.boundaryOutCarryAudio).toBe(false);
    });

    it("derives none for fresh source-only clips but preserves invalid v3 identity for diagnostics", () => {
        const sourceVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 2,
            startSeconds: 0,
            lengthSeconds: 2,
        };
        const fresh = normalizeClip(
            { sourceVideo, stages: [] },
            getRootDefaults,
            getDefaultStageModel,
        );
        const invalid = normalizeClip(
            {
                architecture: "removed-architecture",
                modelProfileId: "removed-profile",
                sourceVideo,
                stages: [],
            },
            getRootDefaults,
            getDefaultStageModel,
        );

        expect(fresh).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
        });
        expect(invalid).toMatchObject({
            architecture: "removed-architecture",
            modelProfileId: "removed-profile",
        });
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
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.boundaryOut).toBe(expected);
    });

    it("normalizeClip ignores a noncanonical PascalCase boundary key", () => {
        const clip = normalizeClip(
            { stages: [{ model: "ltx" }], BoundaryOut: "continue" },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.boundaryOut).toBe("cut");
    });

    it("normalizeClip pads refStrengths for each stage from raw", () => {
        const rawClip: Record<string, unknown> = {
            duration: 2,
            refs: [{ source: REF_SOURCE_BASE, frame: 1 }],
            stages: [
                {
                    model: "ltx",
                    refStrengths: [0.3],
                },
            ],
        };
        const clip = normalizeClip(
            rawClip,
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.refs).toHaveLength(1);
        expect(clip.stages[0].refStrengths).toEqual([0.3]);
    });

    it("normalizeClip clamps and defaults stage ControlNet strength", () => {
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx", controlNetStrength: 1.5 }, {}],
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.stages[0].controlNetStrength).toBe(1);
        expect(clip.stages[1].controlNetStrength).toBe(1);

        const defaultClip = buildDefaultClip(
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(defaultClip.stages[0].controlNetStrength).toBe(0.8);

        const rawDefaultClip = normalizeClip(
            {
                stages: [{}],
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(rawDefaultClip.stages[0].controlNetStrength).toBe(0.8);
    });

    it("buildDefaultClip copies base settings from a previous clip", () => {
        const prev = buildDefaultClip(getRootDefaults, getDefaultStageModel);
        prev.duration = 3;
        prev.boundaryOut = "continue";
        prev.boundaryOutOverlap = 16;
        prev.stages[0].sampler = "res_multistep";
        prev.stages[0].scheduler = "sgm_uniform";
        prev.stages[0].model = "ltx-big";
        prev.stages[0].steps = 20;
        prev.stages[0].cfgScale = 4;
        prev.loras = [{ name: "look" }];
        prev.stages[0].loraWeights = [0.6];

        const clip = buildDefaultClip(
            getRootDefaults,
            getDefaultStageModel,
            false,
            prev,
        );
        expect(clip.duration).toBe(3);
        const stage = clip.stages[0];
        expect(stage.sampler).toBe("res_multistep");
        expect(stage.scheduler).toBe("sgm_uniform");
        expect(stage.model).toBe("ltx-big");
        expect(stage.steps).toBe(20);
        expect(stage.cfgScale).toBe(4);
        expect(clip.loras).toEqual([{ name: "look" }]);
        expect(clip.loras).not.toBe(prev.loras);
        expect(stage.loraWeights).toEqual([0.6]);
        expect(stage.loraWeights).not.toBe(prev.stages[0].loraWeights);
        // The new clip is the trailing one — its own join stays the default.
        expect(clip.boundaryOut).toBe("cut");
        expect(clip.prompt).toBe("");
        expect(clip.refs).toEqual([]);
    });

    it("normalizeClip ignores removed single-ControlNet IC-LoRA fields", () => {
        const defaultClip = normalizeClip(
            {},
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(defaultClip.icLoras).toEqual([]);

        const controlNetRaw = {
            ControlNetSource: "controlnet3",
            ControlNetLora: " ltx-ic-lora.safetensors ",
        };
        const controlNetClip = normalizeClip(
            controlNetRaw,
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(controlNetClip.icLoras).toEqual([]);
    });

    it("normalizeClip maps Swarm (None) legacy ControlNet LoRA token to no entries", () => {
        const clip = normalizeClip(
            { controlNetLora: " ( None ) " },
            getRootDefaults,
            getDefaultStageModel,
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
                        hdr: false,
                        driveMedia: {
                            data: "data:video/mp4;base64,QUJD",
                            fileName: "d.mp4",
                        },
                    },
                    { lora: "" },
                ],
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.icLoras).toHaveLength(1);
        const entry = clip.icLoras[0];
        expect(entry.lora).toBe("detail-lora.safetensors");
        expect(entry.preset).toBe("water-simulation");
        expect(entry.driveSource).toBe("Upload");
        expect(entry.driveData).toBe("visual");
        expect(entry.strength).toBe(2);
        expect(entry.attentionStrength).toBe(0);
        expect(entry.controlType).toBe("depth");
        expect(entry.driveMedia).toEqual({
            data: "data:video/mp4;base64,QUJD",
            fileName: "d.mp4",
        });
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
            getRootDefaults,
            getDefaultStageModel,
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
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.icLoras[0].driveSource).toBe("Incoming");
        expect(clip.icLoras[0].stage).toBe(-1);
    });

    it("normalizeClip keeps Control 0 (passthrough) on sourced and refine stages", () => {
        // Control 0 = passthrough (no sampler): a sourced clip joining others
        // without changes authors 0 on its stage 0, and refine stages may skip too.
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx", control: 0 }, { control: 0 }],
                sourceVideo: {
                    data: "data:video/mp4;base64,QUJD",
                    lengthSeconds: 3,
                },
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.stages[0].control).toBe(0);
        expect(clip.stages[1].control).toBe(0);
    });

    it("normalizeClip keeps an Incoming source at stage 0 on a sourced clip", () => {
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx" }],
                sourceVideo: {
                    data: "data:video/mp4;base64,QUJD",
                    lengthSeconds: 3,
                },
                icLoras: [{ lora: "a", stage: 0, driveSource: "Incoming" }],
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        // Stage 0's incoming frames ARE the footage on a sourced clip.
        expect(clip.sourceVideo).not.toBeNull();
        expect(clip.icLoras[0].driveSource).toBe("Incoming");
        expect(clip.icLoras[0].stage).toBe(0);
    });

    it("normalizeClip does not silently rewrite unavailable Incoming media", () => {
        const clip = normalizeClip(
            {
                stages: [{ model: "ltx" }],
                icLoras: [{ lora: "a", stage: 0, driveSource: "Incoming" }],
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.icLoras[0].driveSource).toBe("Incoming");
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
            getRootDefaults,
            getDefaultStageModel,
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
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.icLoras).toHaveLength(1);
        expect(clip.icLoras[0].lora).toBe("new-lora");
        expect(clip.icLoras[0].driveSource).toBe("Upload");
    });

    it("normalizeClip lets audio length override stored ControlNet length", () => {
        const clip = normalizeClip(
            {
                audioSource: "Upload",
                controlNetLora: "ltx-ic-lora.safetensors",
                clipLengthFromAudio: true,
                clipLengthFromControlNet: true,
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.clipLengthFromAudio).toBe(true);
        expect(clip.clipLengthFromControlNet).toBe(false);
    });

    it("normalizeClip preserves a length flag its current source cannot honor", () => {
        const clip = normalizeClip(
            {
                audioSource: "Native",
                clipLengthFromAudio: true,
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.clipLengthFromAudio).toBe(true);
    });

    it("normalizeClip preserves ControlNet length without a slot-sourced IC-LoRA", () => {
        const clip = normalizeClip(
            {
                audioSource: "Native",
                clipLengthFromControlNet: true,
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.icLoras).toEqual([]);
        expect(clip.clipLengthFromControlNet).toBe(true);
    });

    it("normalizeClip drops ControlNet length when audio length wins", () => {
        const clip = normalizeClip(
            {
                audioSource: "Upload",
                controlNetLora: "(None)",
                clipLengthFromAudio: true,
                clipLengthFromControlNet: true,
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.icLoras).toEqual([]);
        expect(clip.clipLengthFromAudio).toBe(true);
        expect(clip.clipLengthFromControlNet).toBe(false);
    });

    it("normalizeClip preserves 'ControlNet' audio source when controlNetLora is set", () => {
        const clip = normalizeClip(
            {
                audioSource: "ControlNet",
                controlNetLora: "ltx-ic-lora.safetensors",
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.audioSource).toBe("ControlNet");
    });

    it("normalizeClip preserves an unsupported persisted ControlNet audio source", () => {
        const clip = normalizeClip(
            {
                audioSource: "ControlNet",
                controlNetLora: "",
            },
            getRootDefaults,
            getDefaultStageModel,
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
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.clipLengthFromAudio).toBe(true);
    });

    it("normalizeClip ignores removed camelCase single-ControlNet fields", () => {
        const clip = normalizeClip(
            {
                controlNetSource: "ControlNet 2",
                controlNetLora: " detail-lora.safetensors ",
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.icLoras).toEqual([]);
    });

    it("normalizeStage reads canonical upscale fields for non-first stage", () => {
        const stage0 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            { ...minimalStageRaw },
            null,
            0,
            0,
        );
        const stage1 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
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
            getRootDefaults,
            getDefaultStageModel,
            { ...minimalStageRaw },
            null,
            0,
            0,
        );
        const snap = (upscale: number): number =>
            normalizeStage(
                getRootDefaults,
                getDefaultStageModel,
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
            getRootDefaults,
            getDefaultStageModel,
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

    it("normalizeStage keeps authored control/upscale on a sourced clip stage 0", () => {
        const stage0 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
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

        // A sourced stage 0 refines its footage (init-video img2img): authored
        // Control/Upscale/UpscaleMethod survive, still clamped and 0.25-snapped.
        expect(stage0.control).toBe(0.4);
        expect(stage0.upscale).toBe(1.25);
        expect(stage0.upscaleMethod).toBe("latentmodel-b.safetensors");
    });

    it("normalizeClip keeps authored stage-0 refine params for a sourced clip", () => {
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
                sourceVideo: {
                    data: "data:video/mp4;base64,QUJD",
                    lengthSeconds: 3,
                },
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.sourceVideo).not.toBeNull();
        expect(clip.stages[0].control).toBe(0.3);
        expect(clip.stages[0].upscale).toBe(2);
        expect(clip.stages[0].upscaleMethod).toBe("latentmodel-b.safetensors");
    });

    it("normalizeStage reads canonical control for non-first stage", () => {
        const stage0 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            { ...minimalStageRaw },
            null,
            0,
            0,
        );
        const stage1 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
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
        expect(ref.source).toBe(REF_SOURCE_REFINER);
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
                refs: [{ source: REF_SOURCE_REFINER, frame: 2 }],
                stages: [{ refStrengths: [0.3] }, { refStrengths: [0.7] }],
            },
            getRootDefaults,
            getDefaultStageModel,
        );

    it("appendRefToClip adds the ref and pads every stage's refStrengths", () => {
        const clip = twoStageClip();
        appendRefToClip(clip, buildDefaultRef(REF_SOURCE_BASE));
        expect(clip.refs).toHaveLength(2);
        expect(clip.refs[1].source).toBe(REF_SOURCE_BASE);
        expect(clip.stages[0].refStrengths).toHaveLength(2);
        expect(clip.stages[1].refStrengths).toHaveLength(2);
        // Existing strengths preserved; the appended one uses the default 0.8.
        expect(clip.stages[0].refStrengths[0]).toBe(0.3);
        expect(clip.stages[1].refStrengths[0]).toBe(0.7);
        expect(clip.stages[0].refStrengths[1]).toBe(0.8);
    });

    it("removeRefAt removes the ref and the matching strength from every stage", () => {
        const clip = twoStageClip();
        appendRefToClip(clip, buildDefaultRef(REF_SOURCE_BASE));
        expect(removeRefAt(clip, 0)).toBe(true);
        expect(clip.refs).toHaveLength(1);
        expect(clip.refs[0].source).toBe(REF_SOURCE_BASE);
        expect(clip.stages[0].refStrengths).toEqual([0.8]);
        expect(clip.stages[1].refStrengths).toEqual([0.8]);
    });

    it("removeRefAt returns false and leaves the clip untouched for an out-of-range index", () => {
        const clip = twoStageClip();
        expect(removeRefAt(clip, 5)).toBe(false);
        expect(removeRefAt(clip, -1)).toBe(false);
        expect(clip.refs).toHaveLength(1);
        expect(clip.stages[0].refStrengths).toHaveLength(1);
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

    it("normalizeStage aligns legacy LoRAs to clip definitions", () => {
        const stage = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            { ...minimalStageRaw, loras: [{ name: "l.safetensors" }] },
            null,
            0,
            0,
            false,
            [{ name: "l.safetensors" }],
            [0],
        );
        expect(stage.loraWeights).toEqual([1]);
    });

    it("buildDefaultStage inherits a copy of the previous stage's weights", () => {
        const first = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            {
                ...minimalStageRaw,
                loras: [{ name: "x.safetensors", weight: 0.7 }],
            },
            null,
            0,
            0,
            false,
            [{ name: "x.safetensors" }],
            [0],
        );
        const next = buildDefaultStage(
            getRootDefaults,
            getDefaultStageModel,
            first,
            0,
        );
        expect(next.loraWeights).toEqual([0.7]);
        next.loraWeights[0] = 0.1;
        expect(first.loraWeights[0]).toBe(0.7);
    });

    it("normalizeClip round-trips loras across a multi-stage clip", () => {
        const clip = normalizeClip(
            {
                duration: 2,
                refs: [],
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
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.loras).toEqual([
            { name: "base.safetensors" },
            { name: "refine.safetensors" },
        ]);
        expect(clip.stages[0].loraWeights).toEqual([1, 0]);
        expect(clip.stages[1].loraWeights).toEqual([0, 0.4]);
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
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(withRetake.retake).toEqual({
            startSeconds: 1,
            lengthSeconds: 2,
            strength: 0.5,
        });

        const withoutRetake = normalizeClip(
            { duration: 5, stages: [minimalStageRaw] },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(withoutRetake.retake).toBeNull();
    });
});

describe("normalizeSourceVideo", () => {
    const data = "data:video/mp4;base64,QUJD";

    it("keeps a valid source video and rounds its seconds", () => {
        expect(
            normalizeSourceVideo({
                data,
                fileName: "shot.mp4",
                fps: 24,
                durationSeconds: 10.04,
                startSeconds: 2.04,
                lengthSeconds: 5.06,
            }),
        ).toEqual({
            data,
            fileName: "shot.mp4",
            fps: 24,
            durationSeconds: 10,
            startSeconds: 2,
            lengthSeconds: 5.1,
        });
    });

    it("rejects a missing data blob or non-record value", () => {
        expect(normalizeSourceVideo(null)).toBeNull();
        expect(normalizeSourceVideo("x")).toBeNull();
        expect(
            normalizeSourceVideo({ data: " ", lengthSeconds: 2 }),
        ).toBeNull();
    });

    it("clamps the range inside a known file duration", () => {
        expect(
            normalizeSourceVideo({
                data,
                fps: 0,
                durationSeconds: 6,
                startSeconds: 9,
                lengthSeconds: 4,
            }),
        ).toEqual({
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
            normalizeSourceVideo({
                data,
                durationSeconds: 8,
                startSeconds: 3,
            }),
        ).toEqual({
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
            normalizeSourceVideo({ data, lengthSeconds: 3 })?.lengthSeconds,
        ).toBe(3);
        expect(normalizeSourceVideo({ data })).toBeNull();
    });

    it("drives the clip duration from the source range in normalizeClip", () => {
        const clip = normalizeClip(
            {
                duration: 2,
                stages: [minimalStageRaw],
                sourceVideo: {
                    data,
                    durationSeconds: 10,
                    startSeconds: 1,
                    lengthSeconds: 3.5,
                },
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.sourceVideo?.startSeconds).toBe(1);
        expect(clip.duration).toBe(3.5);
    });
});
