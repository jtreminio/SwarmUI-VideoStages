import { describe, expect, it } from "@jest/globals";
import {
    buildDefaultClip,
    buildDefaultRef,
    normalizeClip,
    normalizeRef,
    normalizeStage,
    readRawStageProp,
    readRawStageString,
} from "./normalization";
import {
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    type RootDefaults,
} from "./types";

const getRootDefaults = (): RootDefaults => ({
    modelValues: ["ltx"],
    modelLabels: ["LTX"],
    loraValues: ["ltx-ic-lora.safetensors"],
    loraLabels: ["LTX IC LoRA"],
    vaeValues: ["Automatic"],
    vaeLabels: ["Automatic"],
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
    controlMin: 0.05,
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
    it("readRawStageProp prefers camelCase then PascalCase", () => {
        expect(
            readRawStageProp(
                { control: 0.5, Control: 0.9 },
                "control",
                "Control",
            ),
        ).toBe(0.5);
        expect(readRawStageProp({ Control: 0.9 }, "control", "Control")).toBe(
            0.9,
        );
    });

    it("readRawStageString returns undefined for blank strings", () => {
        expect(
            readRawStageString(
                { upscaleMethod: "  " },
                "upscaleMethod",
                "UpscaleMethod",
            ),
        ).toBeUndefined();
    });

    it("normalizeRef clamps frame to max", () => {
        const ref = normalizeRef({ source: REF_SOURCE_BASE, frame: 999 }, 10);
        expect(ref.frame).toBe(10);
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

    it("normalizeClip defaults and normalizes ControlNet source", () => {
        const defaultClip = normalizeClip(
            {},
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(defaultClip.controlNetSource).toBe("ControlNet 1");
        expect(defaultClip.controlNetLora).toBe("");

        const controlNetRaw = {
            ControlNetSource: "controlnet3",
            ControlNetLora: " ltx-ic-lora.safetensors ",
        };
        const controlNetClip = normalizeClip(
            controlNetRaw,
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(controlNetClip.controlNetSource).toBe("ControlNet 3");
        expect(controlNetClip.controlNetLora).toBe("ltx-ic-lora.safetensors");
    });

    it("normalizeClip maps Swarm (None) ControlNet LoRA token to empty", () => {
        const clip = normalizeClip(
            { controlNetLora: " ( None ) " },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.controlNetLora).toBe("");
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

    it("normalizeClip ignores ControlNet length when ControlNet LoRA is blank", () => {
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
        expect(clip.controlNetLora).toBe("");
        expect(clip.clipLengthFromAudio).toBe(true);
        expect(clip.clipLengthFromControlNet).toBe(false);
    });

    it("normalizeClip reads camelCase controlNetSource and controlNetLora from stored JSON", () => {
        const clip = normalizeClip(
            {
                controlNetSource: "ControlNet 2",
                controlNetLora: " detail-lora.safetensors ",
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.controlNetSource).toBe("ControlNet 2");
        expect(clip.controlNetLora).toBe("detail-lora.safetensors");
    });

    it("normalizeStage reads PascalCase upscale fields for non-first stage", () => {
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
                Upscale: 2,
                UpscaleMethod: "latentmodel-b.safetensors",
            },
            stage0,
            0,
            1,
        );
        expect(stage1.upscale).toBe(2);
        expect(stage1.upscaleMethod).toBe("latentmodel-b.safetensors");
    });

    it("normalizeStage forces first-stage control to the root default", () => {
        const stage0 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            {
                ...minimalStageRaw,
                Control: 0.4,
            },
            null,
            0,
            0,
        );

        expect(stage0.control).toBe(0.5);
    });

    it("normalizeStage reads PascalCase control for non-first stage", () => {
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
                Control: 0.4,
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

    it("normalizeClip trims WAN clips to two refs and locks first/last frame semantics", () => {
        const clip = normalizeClip(
            {
                duration: 3,
                refs: [
                    {
                        source: REF_SOURCE_BASE,
                        frame: 8,
                        fromEnd: true,
                    },
                    {
                        source: REF_SOURCE_REFINER,
                        frame: 5,
                        fromEnd: false,
                    },
                    {
                        source: REF_SOURCE_BASE,
                        frame: 2,
                        fromEnd: true,
                    },
                ],
                stages: [
                    {
                        ...minimalStageRaw,
                        model: "wan-2_2-image2video-14b",
                    },
                ],
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.refs).toHaveLength(2);
        expect(clip.refs[0].frame).toBe(1);
        expect(clip.refs[0].fromEnd).toBe(false);
        expect(clip.refs[1].frame).toBe(1);
        expect(clip.refs[1].fromEnd).toBe(true);
    });
});
