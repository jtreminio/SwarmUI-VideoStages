import { describe, expect, it } from "@jest/globals";
import {
    buildDefaultRef,
    normalizeClip,
    normalizeRef,
    normalizeStage,
    readRawStageProp,
    readRawStageString,
} from "./normalization";
import { REF_SOURCE_BASE, type RootDefaults } from "./Types";

const stubDefaults = (): RootDefaults => ({
    modelValues: ["ltx"],
    modelLabels: ["LTX"],
    vaeValues: ["Automatic"],
    vaeLabels: ["Automatic"],
    samplerValues: ["euler"],
    samplerLabels: ["Euler"],
    schedulerValues: ["normal"],
    schedulerLabels: ["Normal"],
    upscaleMethodValues: ["pixel-lanczos", "pixel-bicubic"],
    upscaleMethodLabels: ["Lanczos", "Bicubic"],
    width: 1024,
    height: 768,
    fps: 24,
    frames: 48,
    control: 1,
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

const getRootDefaults = (): RootDefaults => stubDefaults();
const getDefaultStageModel = (modelValues: string[]): string =>
    modelValues[0] ?? "";

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
            0,
            getRootDefaults,
            getDefaultStageModel,
        );
        expect(clip.refs).toHaveLength(1);
        expect(clip.stages[0].refStrengths).toEqual([0.3]);
    });

    it("normalizeStage reads PascalCase upscale fields for non-first stage", () => {
        const stage0 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            {
                model: "ltx",
                sampler: "euler",
                scheduler: "normal",
            },
            null,
            0,
            0,
        );
        const stage1 = normalizeStage(
            getRootDefaults,
            getDefaultStageModel,
            {
                Upscale: 2,
                UpscaleMethod: "pixel-bicubic",
                model: "ltx",
                sampler: "euler",
                scheduler: "normal",
            },
            stage0,
            0,
            1,
        );
        expect(stage1.upscale).toBe(2);
        expect(stage1.upscaleMethod).toBe("pixel-bicubic");
    });

    it("buildDefaultRef matches editor defaults", () => {
        const ref = buildDefaultRef();
        expect(ref.source).toBe(REF_SOURCE_BASE);
        expect(ref.uploadedImage).toBeNull();
    });
});
