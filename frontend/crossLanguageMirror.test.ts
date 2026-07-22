/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";

import { describe, expect, it } from "@jest/globals";

import { crossfadePlanForClips } from "./boundaryPlan";
import { IC_LORA_PRESETS, icLoraAutoModelName } from "./icLoraPresets";
import { framesForClip } from "./renderUtils";
import type { Clip } from "./types";

// Frontend halves of the cross-language drift tests. Each asserts against the same JSON fixture in
// Tests/fixtures/ that the C# CrossLanguageMirrorTests reads, so a deliberate constant change on
// either side breaks the pair.
const fixturesDir = path.resolve(__dirname, "..", "Tests", "fixtures");
const loadFixture = <T>(name: string): T =>
    JSON.parse(fs.readFileSync(path.join(fixturesDir, name), "utf8")) as T;

describe("cross-language mirror: M2 frame alignment (renderUtils.framesForClip)", () => {
    interface FrameCase {
        durationSeconds: number;
        fps: number;
        expectedFrames: number;
    }
    const cases = loadFixture<FrameCase[]>("frame-align-cases.json");

    it.each(
        cases,
    )("duration=$durationSeconds fps=$fps -> $expectedFrames frames", ({
        durationSeconds,
        fps,
        expectedFrames,
    }) => {
        expect(framesForClip(durationSeconds, fps)).toBe(expectedFrames);
    });
});

describe("cross-language mirror: M1 crossfade plan (boundaryPlan.crossfadePlanForClips)", () => {
    interface PlanCase {
        name: string;
        frames: number[];
        boundaries: string[];
        boundaryOverlaps: number[];
        expectedOverlaps: number[];
        expectedFallback: boolean;
    }
    const cases = loadFixture<PlanCase[]>("crossfade-plan-cases.json");

    // The frontend derives frame counts from duration via framesForClip; feed a duration (at fps 1)
    // that reproduces the fixture's exact frame count so both sides plan on identical inputs.
    const clipFor = (
        frames: number,
        boundaryOut: string,
        boundaryOutOverlap: number,
    ): Clip =>
        ({
            duration: frames - 1,
            boundaryOut,
            boundaryOutOverlap,
            stages: [],
            refs: [],
        }) as unknown as Clip;

    it.each(cases)("$name", ({
        frames,
        boundaries,
        boundaryOverlaps,
        expectedOverlaps,
        expectedFallback,
    }) => {
        for (const f of frames) {
            expect(framesForClip(f - 1, 1)).toBe(f);
        }
        const clips = frames.map((f, i) =>
            clipFor(f, boundaries[i], boundaryOverlaps[i]),
        );
        const plan = crossfadePlanForClips(clips, 1);
        expect(plan.overlaps).toEqual(expectedOverlaps);
        expect(plan.fallback).toBe(expectedFallback);
    });
});

describe("cross-language mirror: M4 IC-LoRA auto-model naming (icLoraPresets)", () => {
    interface PresetCase {
        id: string;
        weightsUrl: string;
        autoModelName: string;
    }
    const cases = loadFixture<PresetCase[]>("ic-lora-presets.json");

    it("presets match the shared fixture id set", () => {
        expect(IC_LORA_PRESETS.map((p) => p.id).sort()).toEqual(
            cases.map((c) => c.id).sort(),
        );
    });

    it.each(cases)("$id url + auto-model name match the fixture", ({
        id,
        weightsUrl,
        autoModelName,
    }) => {
        const preset = IC_LORA_PRESETS.find((p) => p.id === id);
        expect(preset).toBeDefined();
        expect(preset!.weightsUrl).toBe(weightsUrl);
        expect(icLoraAutoModelName(preset!)).toBe(autoModelName);
    });
});
