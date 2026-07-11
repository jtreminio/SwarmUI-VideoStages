import { describe, expect, it } from "@jest/globals";
import {
    appendRefToClip,
    buildDefaultClip,
    buildDefaultRef,
    buildDefaultStage,
    normalizeClip,
    normalizeRef,
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
    modelValues: ["ltx"],
    modelLabels: ["LTX"],
    loraValues: ["ltx-ic-lora.safetensors"],
    loraLabels: ["LTX IC LoRA"],
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

    it("normalizeStageRefStrengthValue accepts 0 without clamping up", () => {
        expect(normalizeStageRefStrengthValue(0)).toBe(0);
        expect(normalizeStageRefStrengthValue("0")).toBe(0);
        expect(normalizeStageRefStrengthValue(-0.5)).toBe(0);
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

    it("normalizeClip falls back to Native when ControlNet audio source is stored without controlNetLora", () => {
        const clip = normalizeClip(
            {
                audioSource: "ControlNet",
                controlNetLora: "",
            },
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.audioSource).toBe("Native");
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

describe("stage loras", () => {
    it("normalizeStageLoras parses tolerant entries and drops invalid ones", () => {
        expect(
            normalizeStageLoras([
                { name: "a.safetensors", weight: 0.5 },
                { Name: "b.safetensors", Weight: "1.25" },
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

    it("normalizeStage reads loras (camel or Pascal) into the stage", () => {
        const stage = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            { ...minimalStageRaw, Loras: [{ Name: "l.safetensors" }] },
            null,
            0,
            0,
        );
        expect(stage.loras).toEqual([{ name: "l.safetensors", weight: 1 }]);
    });

    it("buildDefaultStage inherits (deep-copies) the previous stage's loras", () => {
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
        );
        const next = buildDefaultStage(
            getRootDefaults,
            getDefaultStageModel,
            first,
            0,
        );
        expect(next.loras).toEqual([{ name: "x.safetensors", weight: 0.7 }]);
        // Deep copy: mutating the child must not touch the parent.
        next.loras[0].weight = 0.1;
        expect(first.loras[0].weight).toBe(0.7);
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
        expect(clip.stages[0].loras).toEqual([
            { name: "base.safetensors", weight: 1 },
        ]);
        expect(clip.stages[1].loras).toEqual([
            { name: "refine.safetensors", weight: 0.4 },
        ]);
    });
});
