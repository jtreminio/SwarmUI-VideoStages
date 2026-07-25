import { describe, expect, it } from "@jest/globals";

import {
    fakeArchitectureCatalog,
    testArchitectureCapabilities,
} from "../__test_helpers__/architectureFixtures";
import {
    hdrIcLoraFixture,
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { planArchitectureConversion } from "./conversion/plan";

/** Supports IC-LoRAs (and HDR) but not frame references. */
const icLoraOnlyCatalog = () => {
    const catalog = fakeArchitectureCatalog();
    catalog.architectures[0].capabilities = testArchitectureCapabilities({
        clip: ["prompts", "audio-sources"],
        stage: ["ic-lora", "hdr"],
        upscaleModes: [],
        audioSourceKinds: ["Native"],
    });
    return catalog;
};

const targetFor = (catalog: ReturnType<typeof icLoraOnlyCatalog>) => ({
    architectureId: "test-video",
    modelProfileId: "test-profile",
    model: "test-video.safetensors",
    capabilities: catalog.architectures[0].capabilities,
});

describe("conversion feature-state ownership", () => {
    it("keeps IC-LoRA strengths when only frame references are dropped", () => {
        const catalog = icLoraOnlyCatalog();
        const clip = minimalClip({
            icLoras: [hdrIcLoraFixture({ hdr: false, preset: "pose" })],
            refs: [minimalRef()],
            stages: [
                minimalStage({
                    refStrengths: [0.7],
                    icLoraStrengths: [0.42],
                }),
            ],
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion).not.toBeNull();
        expect(conversion?.clip.refs).toEqual([]);
        expect(conversion?.clip.stages[0].refStrengths).toEqual([]);
        expect(conversion?.clip.icLoras).toHaveLength(1);
        expect(conversion?.clip.stages[0].icLoraStrengths).toEqual([0.42]);
    });

    it("drops the strengths of the IC-LoRA entries it removes, by position", () => {
        const catalog = fakeArchitectureCatalog();
        // IC-LoRAs supported, HDR not: only the HDR entry is removed.
        catalog.architectures[0].capabilities = testArchitectureCapabilities({
            clip: ["prompts", "audio-sources"],
            stage: ["ic-lora"],
            upscaleModes: [],
            audioSourceKinds: ["Native"],
        });
        const clip = minimalClip({
            architecture: "ltx2",
            icLoras: [
                hdrIcLoraFixture({ hdr: false, preset: "pose" }),
                hdrIcLoraFixture(),
                hdrIcLoraFixture({ hdr: false, preset: "depth" }),
            ],
            stages: [minimalStage({ icLoraStrengths: [0.1, 0.2, 0.3] })],
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion?.removals).toContain("1 HDR IC-LoRA");
        expect(conversion?.clip.icLoras.map((entry) => entry.preset)).toEqual([
            "pose",
            "depth",
        ]);
        expect(conversion?.clip.stages[0].icLoraStrengths).toEqual([0.1, 0.3]);
    });

    it("clears IC-LoRA strengths when IC-LoRAs are unsupported", () => {
        const catalog = fakeArchitectureCatalog();
        const clip = minimalClip({
            icLoras: [hdrIcLoraFixture({ hdr: false, preset: "pose" })],
            stages: [minimalStage({ icLoraStrengths: [0.42] })],
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion?.clip.icLoras).toEqual([]);
        expect(conversion?.clip.stages[0].icLoraStrengths).toEqual([]);
    });
});
