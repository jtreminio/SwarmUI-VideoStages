import { describe, expect, it } from "@jest/globals";

import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalStage,
    sourceVideoFixture,
} from "../__test_helpers__/clipFixtures";
import {
    resolveClipFrameGrid,
    resolveCompatibleFrameGrid,
    resolvedClipFrameGrid,
} from "./temporalGrid";

describe("resolved temporal grid", () => {
    it("uses the least common grid for every active resolved stage", () => {
        const catalog = testArchitectureCatalog();
        catalog.entries.push({
            ...catalog.entries[0],
            value: "six-grid.safetensors",
            frameGrid: 6,
        });
        const clip = minimalClip({
            stages: [
                minimalStage({ model: "six-grid.safetensors" }),
                minimalStage({ model: "ltx" }),
            ],
        });

        expect(resolveCompatibleFrameGrid([6, 8])).toEqual({
            status: "resolved",
            frameGrid: 24,
        });
        expect(resolvedClipFrameGrid(clip, catalog)).toBe(24);
    });

    it("ignores dormant stages after the first skip marker", () => {
        const catalog = testArchitectureCatalog();
        catalog.entries.push({
            ...catalog.entries[0],
            value: "six-grid.safetensors",
            frameGrid: 6,
        });
        const clip = minimalClip({
            stages: [
                minimalStage({ model: "six-grid.safetensors" }),
                minimalStage({ model: "ltx", skipped: true }),
            ],
        });

        expect(resolvedClipFrameGrid(clip, catalog)).toBe(6);
    });

    it("excludes sourced and trailing passthrough handlers from the compatible grid", () => {
        const catalog = testArchitectureCatalog();
        catalog.entries.push({
            ...catalog.entries[0],
            value: "six-grid.safetensors",
            frameGrid: 6,
        });
        const sourcedPassthrough = minimalClip({
            sourceVideo: sourceVideoFixture(),
            stages: [minimalStage({ model: "ltx", control: 0 })],
        });
        const generatedThenPassthrough = minimalClip({
            stages: [
                minimalStage({
                    model: "six-grid.safetensors",
                    control: 1,
                }),
                minimalStage({ model: "ltx", control: 0 }),
            ],
        });

        expect(resolveClipFrameGrid(sourcedPassthrough, catalog)).toEqual({
            status: "not-applicable",
        });
        expect(resolvedClipFrameGrid(generatedThenPassthrough, catalog)).toBe(
            6,
        );
    });

    it("does not claim a static grid for runtime-derived clip lengths", () => {
        const clip = minimalClip({
            clipLengthFromAudio: true,
            stages: [minimalStage({ model: "ltx" })],
        });

        expect(resolveClipFrameGrid(clip, testArchitectureCatalog())).toEqual({
            status: "not-applicable",
        });
    });

    it("uses the static grid when a dynamic-length capability is absent", () => {
        const catalog = testArchitectureCatalog();
        const descriptor = catalog.architectures[0];
        descriptor.capabilities.clip = descriptor.capabilities.clip.filter(
            (feature) => feature !== "audio-derived-duration",
        );
        const clip = minimalClip({
            clipLengthFromAudio: true,
            stages: [minimalStage({ model: "ltx" })],
        });

        expect(resolveClipFrameGrid(clip, catalog)).toEqual({
            status: "resolved",
            frameGrid: 8,
        });
    });

    it("does not activate a terminal retake without source footage", () => {
        const catalog = testArchitectureCatalog();
        catalog.entries.push(
            {
                ...catalog.entries[0],
                value: "grid-50000.safetensors",
                frameGrid: 50_000,
            },
            {
                ...catalog.entries[0],
                value: "grid-50001.safetensors",
                frameGrid: 50_001,
            },
        );
        const clip = minimalClip({
            retake: {
                startSeconds: 0,
                lengthSeconds: 1,
                strength: 0.5,
            },
            stages: [
                minimalStage({
                    model: "grid-50000.safetensors",
                    control: 1,
                }),
                minimalStage({
                    model: "grid-50001.safetensors",
                    control: 0,
                }),
            ],
        });

        expect(resolveClipFrameGrid(clip, catalog)).toEqual({
            status: "resolved",
            frameGrid: 50_000,
        });
    });

    it("stays neutral for admission-blocked authored models and upscale modes", () => {
        const catalog = testArchitectureCatalog();
        const unresolvedSuffix = minimalClip({
            stages: [
                minimalStage({ model: "ltx" }),
                minimalStage({
                    model: "future-model.safetensors",
                    skipped: true,
                }),
            ],
        });
        const unknownUpscale = minimalClip({
            stages: [
                minimalStage({
                    model: "ltx",
                    upscale: 2,
                    upscaleMethod: "future-upscale",
                }),
            ],
        });

        expect(resolveClipFrameGrid(unresolvedSuffix, catalog)).toEqual({
            status: "unknown",
        });
        expect(resolveClipFrameGrid(unknownUpscale, catalog)).toEqual({
            status: "unknown",
        });
    });

    it("does not force a passthrough root while global refine owns the root", () => {
        const clip = minimalClip({
            stages: [minimalStage({ model: "ltx", control: 0 })],
        });

        expect(
            resolveClipFrameGrid(clip, testArchitectureCatalog(), {
                globalRefineMode: true,
            }),
        ).toEqual({ status: "not-applicable" });
    });

    it("stays neutral instead of guessing when any active model is unresolved", () => {
        const clip = minimalClip({
            stages: [
                minimalStage({ model: "ltx" }),
                minimalStage({ model: "future-model.safetensors" }),
            ],
        });

        expect(resolvedClipFrameGrid(clip, testArchitectureCatalog())).toBe(1);
        expect(resolveClipFrameGrid(clip, testArchitectureCatalog())).toEqual({
            status: "unknown",
        });
    });

    it("reports combinations beyond the backend Int32 grid as conflicts", () => {
        expect(resolveCompatibleFrameGrid([50_000, 50_001])).toEqual({
            status: "conflict",
        });
    });
});
