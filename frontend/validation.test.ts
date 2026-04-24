import { afterEach, describe, expect, it } from "@jest/globals";
import { stubBase2EditStageRegistry } from "./__test_helpers__/registries";
import { type Clip, REF_SOURCE_BASE, REF_SOURCE_UPLOAD } from "./types";
import { getRefSourceError, validateClips } from "./validation";

const minimalClip = (overrides: Partial<Clip> = {}): Clip => ({
    expanded: true,
    skipped: false,
    duration: 2,
    audioSource: "Native",
    saveAudioTrack: false,
    uploadedAudio: null,
    refs: [],
    stages: [
        {
            expanded: true,
            skipped: false,
            control: 1,
            refStrengths: [],
            upscale: 1,
            upscaleMethod: "pixel-lanczos",
            model: "m",
            vae: "",
            steps: 8,
            cfgScale: 1,
            sampler: "euler",
            scheduler: "normal",
        },
    ],
    ...overrides,
});

describe("validation", () => {
    afterEach(() => {
        delete window.base2editStageRegistry;
    });

    describe("getRefSourceError", () => {
        it("accepts Base / Refiner / Upload compact names", () => {
            expect(getRefSourceError(REF_SOURCE_BASE)).toBeNull();
            expect(getRefSourceError("Refiner")).toBeNull();
            expect(getRefSourceError(REF_SOURCE_UPLOAD)).toBeNull();
        });

        it("accepts Base2Edit refs when registry includes the stage", () => {
            stubBase2EditStageRegistry(["edit0"]);
            expect(getRefSourceError("edit0")).toBeNull();
        });

        it("errors when Base2Edit ref is missing from registry", () => {
            stubBase2EditStageRegistry([]);
            expect(getRefSourceError("edit0")).toContain("missing Base2Edit");
        });

        it("errors on unknown source", () => {
            expect(getRefSourceError("nope")).toContain("unknown source");
        });
    });

    describe("validateClips", () => {
        it("allows an empty clip list while editing", () => {
            expect(validateClips([])).toEqual([]);
        });

        it("allows clips with no stages while editing", () => {
            expect(validateClips([minimalClip({ stages: [] })])).toEqual([]);
        });

        it("flags missing model/sampler/scheduler", () => {
            const clip = minimalClip({
                stages: [
                    {
                        ...minimalClip().stages[0],
                        model: "",
                        sampler: "",
                        scheduler: "",
                    },
                ],
            });
            const errors = validateClips([clip]);
            expect(
                errors.some((e) => e.includes("missing a video model")),
            ).toBe(true);
            expect(errors.some((e) => e.includes("missing a sampler"))).toBe(
                true,
            );
            expect(errors.some((e) => e.includes("missing a scheduler"))).toBe(
                true,
            );
        });

        it("skips validation for skipped clips and stages", () => {
            const clip = minimalClip({
                skipped: true,
                stages: [
                    {
                        ...minimalClip().stages[0],
                        skipped: true,
                        model: "",
                    },
                ],
            });
            expect(validateClips([clip])).toEqual([]);
        });
    });
});
