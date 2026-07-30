/// <reference types="node" />
import * as fs from "node:fs";
import * as path from "node:path";

import { describe, expect, it } from "@jest/globals";

import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { boundaryOverlapConstraints } from "./architectures/boundaryConstraints";
import { parseVideoArchitectureCatalog } from "./architectures/catalog";
import {
    DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT,
    findIcLoraPreset,
    IC_LORA_PRESETS,
    icLoraAutoModelName,
    icLoraDriveMediaContract,
    icLoraDriveMediaContractForData,
    icLoraLegacyAutoModelName,
} from "./architectures/ltx2/icLoraPresets";
import { upscaleModeForMethod } from "./architectures/policy";
import type { ArchitectureCatalogEntryDto } from "./architectures/types";
import { crossfadePlanForClips } from "./boundaryPlan";
import { dimensionsFor } from "./dimensionPresets";
import { snapDimensions } from "./dimensionSnap";
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

describe("cross-language mirror: upscale method classification", () => {
    interface UpscaleMethodCase {
        method: string;
        expectedMode: string;
    }
    const cases = loadFixture<UpscaleMethodCase[]>("upscale-method-cases.json");

    it.each(cases)("$method -> $expectedMode", ({ method, expectedMode }) => {
        expect(upscaleModeForMethod(method)).toBe(expectedMode);
    });
});

describe("cross-language mirror: dimension snapping", () => {
    interface DimensionCase {
        name: string;
        ratio: string;
        sideLength: number;
        factor: number;
        rawWidth: number;
        rawHeight: number;
        expectedWidth: number;
        expectedHeight: number;
    }
    const cases = loadFixture<DimensionCase[]>("dimension-snap-cases.json");

    it.each(cases)("$name", ({
        ratio,
        sideLength,
        factor,
        rawWidth,
        rawHeight,
        expectedWidth,
        expectedHeight,
    }) => {
        const raw = dimensionsFor(ratio, sideLength);
        expect(raw).toEqual({ width: rawWidth, height: rawHeight });
        expect(snapDimensions(rawWidth, rawHeight, 32 * factor)).toEqual({
            width: expectedWidth,
            height: expectedHeight,
        });
    });
});

describe("cross-language mirror: M2 frame alignment (renderUtils.framesForClip)", () => {
    interface FrameCase {
        durationSeconds: number;
        fps: number;
        frameGrid: number;
        expectedFrames: number;
    }
    const cases = loadFixture<FrameCase[]>("frame-align-cases.json");

    it.each(
        cases,
    )("duration=$durationSeconds fps=$fps -> $expectedFrames frames", ({
        durationSeconds,
        fps,
        frameGrid,
        expectedFrames,
    }) => {
        expect(framesForClip(durationSeconds, fps, frameGrid)).toBe(
            expectedFrames,
        );
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
            expect(framesForClip(f - 1, 1, 8)).toBe(f);
        }
        const clips = frames.map((f, i) =>
            clipFor(f, boundaries[i], boundaryOverlaps[i]),
        );
        const plan = crossfadePlanForClips(
            clips,
            1,
            ltxBoundaryConstraints,
            () => 8,
        );
        expect(plan.overlaps).toEqual(expectedOverlaps);
        expect(plan.fallback).toBe(expectedFallback);
    });
});

describe("cross-language mirror: M4 IC-LoRA auto-model naming (icLoraPresets)", () => {
    interface PresetCase {
        id: string;
        weightsUrl: string;
        autoModelName: string;
        legacyAutoModelName: string;
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
        legacyAutoModelName,
    }) => {
        const preset = IC_LORA_PRESETS.find((p) => p.id === id);
        if (!preset) {
            throw new Error(`preset ${id} not found`);
        }
        expect(preset.weightsUrl).toBe(weightsUrl);
        expect(icLoraAutoModelName(preset)).toBe(autoModelName);
        expect(icLoraLegacyAutoModelName(preset)).toBe(legacyAutoModelName);
    });
});

describe("cross-language mirror: LTX IC-LoRA Drive Media contracts", () => {
    interface DriveMediaContract {
        driveMediaKinds: string[];
        acceptedKinds: string[];
        driveData: string;
    }
    const contract = loadFixture<{
        none: DriveMediaContract;
        visual: DriveMediaContract;
        audio: DriveMediaContract;
        "visual-image-only": DriveMediaContract;
    }>("ic-lora-drive-media-contract.json");

    it("keeps declarative stream semantics aligned with the backend", () => {
        expect(icLoraDriveMediaContractForData("none")).toEqual({
            acceptedKinds: contract.none.acceptedKinds,
            driveData: contract.none.driveData,
        });
        expect(contract.none.driveMediaKinds).toEqual(
            contract.none.acceptedKinds,
        );
        expect(DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT).toEqual({
            acceptedKinds: contract.visual.acceptedKinds,
            driveData: contract.visual.driveData,
        });
        expect(contract.visual.driveMediaKinds).toEqual(
            DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT.acceptedKinds,
        );
        expect(icLoraDriveMediaContract(findIcLoraPreset("lipdub"))).toEqual({
            acceptedKinds: contract.audio.acceptedKinds,
            driveData: contract.audio.driveData,
        });
        expect(contract.audio.driveMediaKinds).toEqual(
            contract.audio.acceptedKinds,
        );
        expect(contract["visual-image-only"]).toMatchObject({
            driveData: "visual",
            driveMediaKinds: ["image"],
            acceptedKinds: ["image"],
        });
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
    const parsed = parseVideoArchitectureCatalog({
        architectures: [contract.descriptor],
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
