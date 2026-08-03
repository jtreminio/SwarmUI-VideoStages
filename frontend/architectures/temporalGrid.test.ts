import { describe, expect, it } from "@jest/globals";

import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import {
    initVideoFixture,
    minimalClip,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { framesForClip } from "../renderUtils";
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

        expect(
            resolveCompatibleFrameGrid([
                { frameGrid: 6, frameGridOrigin: 1 },
                { frameGrid: 8, frameGridOrigin: 1 },
            ]),
        ).toEqual({
            status: "resolved",
            frameGrid: 24,
            frameGridOrigin: 1,
        });
        expect(resolvedClipFrameGrid(clip, catalog)).toEqual({
            frameGrid: 24,
            frameGridOrigin: 1,
        });
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

        expect(resolvedClipFrameGrid(clip, catalog)).toEqual({
            frameGrid: 6,
            frameGridOrigin: 1,
        });
    });

    it("excludes init-video and trailing passthrough handlers from the compatible grid", () => {
        const catalog = testArchitectureCatalog();
        catalog.entries.push({
            ...catalog.entries[0],
            value: "six-grid.safetensors",
            frameGrid: 6,
        });
        const initVideoPassthrough = minimalClip({
            initVideo: initVideoFixture(),
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

        expect(resolveClipFrameGrid(initVideoPassthrough, catalog)).toEqual({
            status: "not-applicable",
        });
        expect(
            resolvedClipFrameGrid(generatedThenPassthrough, catalog),
        ).toEqual({ frameGrid: 6, frameGridOrigin: 1 });
    });

    it("excludes latent-upscale handlers when the architecture cannot run them", () => {
        const catalog = testArchitectureCatalog();
        catalog.entries.push({
            ...catalog.entries[0],
            value: "six-grid.safetensors",
            frameGrid: 6,
        });
        catalog.architectures[0].capabilities.features =
            catalog.architectures[0].capabilities.features.filter(
                (feature) => feature !== "latentUpscale",
            );
        const clip = minimalClip({
            stages: [
                minimalStage({
                    model: "six-grid.safetensors",
                    control: 1,
                }),
                minimalStage({
                    model: "ltx",
                    control: 0,
                    upscale: 2,
                    upscaleMethod: "latentmodel-detail.safetensors",
                }),
            ],
        });

        expect(resolveClipFrameGrid(clip, catalog)).toEqual({
            status: "resolved",
            frameGrid: 6,
            frameGridOrigin: 1,
        });
    });

    it("does not claim a static grid for runtime-derived clip lengths", () => {
        const fromDrivingAudio = minimalClip({
            audioSource: "Upload",
            clipLengthFromAudio: true,
            stages: [minimalStage({ model: "ltx" })],
        });
        const fromControlNet = minimalClip({
            clipLengthFromControlNet: true,
            stages: [minimalStage({ model: "ltx" })],
        });

        expect(
            resolveClipFrameGrid(fromDrivingAudio, testArchitectureCatalog()),
        ).toEqual({ status: "not-applicable" });
        expect(
            resolveClipFrameGrid(fromControlNet, testArchitectureCatalog()),
        ).toEqual({ status: "not-applicable" });
    });

    // Mirrors EffectiveVideoRequestTests.Audio_length_from_a_non_driving_source_still_projects_onto_the_grid:
    // the same authored 27 frames (1.05s at 24fps) snap up to 33 on this grid of 8.
    it("uses the static grid when the audio source cannot drive the length", () => {
        const catalog = testArchitectureCatalog();
        const clip = minimalClip({
            duration: 1.05,
            audioSource: "Native",
            clipLengthFromAudio: true,
            stages: [minimalStage({ model: "ltx" })],
        });

        expect(resolveClipFrameGrid(clip, catalog)).toEqual({
            status: "resolved",
            frameGrid: 8,
            frameGridOrigin: 1,
        });
        expect(
            framesForClip(clip.duration, 24, {
                frameGrid: 1,
                frameGridOrigin: 1,
            }),
        ).toBe(27);
        expect(
            framesForClip(
                clip.duration,
                24,
                resolvedClipFrameGrid(clip, catalog),
            ),
        ).toBe(33);
    });

    it("uses the static grid when a dynamic-length capability is absent", () => {
        const catalog = testArchitectureCatalog();
        const descriptor = catalog.architectures[0];
        descriptor.capabilities.features =
            descriptor.capabilities.features.filter(
                (feature) => feature !== "audioDerivedDuration",
            );
        const clip = minimalClip({
            audioSource: "Upload",
            clipLengthFromAudio: true,
            stages: [minimalStage({ model: "ltx" })],
        });

        expect(resolveClipFrameGrid(clip, catalog)).toEqual({
            status: "resolved",
            frameGrid: 8,
            frameGridOrigin: 1,
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
            frameGridOrigin: 1,
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

    it("stays neutral instead of guessing when any active model is unresolved", () => {
        const clip = minimalClip({
            stages: [
                minimalStage({ model: "ltx" }),
                minimalStage({ model: "future-model.safetensors" }),
            ],
        });

        expect(resolvedClipFrameGrid(clip, testArchitectureCatalog())).toEqual({
            frameGrid: 1,
            frameGridOrigin: 1,
        });
        expect(resolveClipFrameGrid(clip, testArchitectureCatalog())).toEqual({
            status: "unknown",
        });
    });

    it("reports combinations beyond the backend Int32 grid as conflicts", () => {
        expect(
            resolveCompatibleFrameGrid([
                { frameGrid: 50_000, frameGridOrigin: 1 },
                { frameGrid: 50_001, frameGridOrigin: 1 },
            ]),
        ).toEqual({
            status: "conflict",
        });
    });
});
