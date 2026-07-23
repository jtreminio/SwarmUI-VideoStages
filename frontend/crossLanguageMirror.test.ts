/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";

import { describe, expect, it } from "@jest/globals";

import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { boundaryOverlapConstraints } from "./architectures/boundaryConstraints";
import { parseVideoArchitectureCatalog } from "./architectures/catalog";
import { ltx2Architecture } from "./architectures/ltx2/definition";
import {
    IC_LORA_PRESETS,
    icLoraAutoModelName,
} from "./architectures/ltx2/icLoraPresets";
import type { ArchitectureCatalogEntryDto } from "./architectures/types";
import { crossfadePlanForClips } from "./boundaryPlan";
import { framesForClip } from "./renderUtils";
import type { BoundaryOut, Clip } from "./types";

// Frontend halves of the cross-language drift tests. Each asserts against the same JSON fixture in
// Tests/fixtures/ that the C# CrossLanguageMirrorTests reads, so a deliberate constant change on
// either side breaks the pair.
const fixturesDir = path.resolve(__dirname, "..", "Tests", "fixtures");
const loadFixture = <T>(name: string): T =>
    JSON.parse(fs.readFileSync(path.join(fixturesDir, name), "utf8")) as T;
const ltxBoundaryConstraints = (
    _clip: Clip,
    _index: number,
    mode: BoundaryOut,
) =>
    boundaryOverlapConstraints(
        testArchitectureCatalog().architectures[0].boundaryRules[mode],
    );

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
        const plan = crossfadePlanForClips(clips, 1, ltxBoundaryConstraints);
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
        if (!preset) {
            throw new Error(`preset ${id} not found`);
        }
        expect(preset.weightsUrl).toBe(weightsUrl);
        expect(icLoraAutoModelName(preset)).toBe(autoModelName);
    });
});

describe("cross-language mirror: architecture catalog rule contract", () => {
    interface ArchitectureDescriptorContract {
        descriptor: ArchitectureCatalogEntryDto;
        forbiddenProfileCapabilities: string[];
    }

    const contract = loadFixture<ArchitectureDescriptorContract>(
        "architecture-catalog-rule-contract.json",
    );
    const wireArchitecture = {
        id: ltx2Architecture.id,
        label: ltx2Architecture.label,
        defaultProfileId: ltx2Architecture.defaultProfileId,
        capabilities: ltx2Architecture.capabilities,
        profiles: ltx2Architecture.profiles,
        boundaryRules: ltx2Architecture.boundaryRules,
        rules: ltx2Architecture.rules,
    };
    const parsed = parseVideoArchitectureCatalog({
        architectures: [wireArchitecture],
        models: [],
    });

    it("matches the complete backend descriptor through the strict wire parser", () => {
        expect(parsed).not.toBeNull();
        const architecture = parsed?.architectures[0];
        if (!architecture) {
            throw new Error("LTX architecture did not parse");
        }
        expect(architecture).toEqual(contract.descriptor);
        for (const forbidden of contract.forbiddenProfileCapabilities) {
            expect(architecture.profiles[0]?.capabilities).not.toContain(
                forbidden,
            );
        }
    });
});
