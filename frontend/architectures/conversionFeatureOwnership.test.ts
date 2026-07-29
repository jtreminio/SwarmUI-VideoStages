import { describe, expect, it } from "@jest/globals";

import {
    fakeArchitectureCatalog,
    testArchitectureCapabilities,
    testArchitectureCatalog,
} from "../__test_helpers__/architectureFixtures";
import {
    hdrIcLoraFixture,
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { planArchitectureConversion } from "./conversion/plan";
import { deriveArchitectureDiagnostics } from "./diagnostics";

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

    it("drops audio reuse without dropping a supported uploaded audio source", () => {
        const catalog = icLoraOnlyCatalog();
        catalog.architectures[0].capabilities.audioSourceKinds = ["Upload"];
        const clip = minimalClip({
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            reuseAudio: true,
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion?.clip.audioSource).toBe("Upload");
        expect(conversion?.clip.uploadedAudio).toEqual(clip.uploadedAudio);
        expect(conversion?.clip.reuseAudio).toBe(false);
        expect(conversion?.removals).toContain("captured stage audio reuse");
        expect(conversion?.removals).not.toContain(
            "clip audio source settings",
        );
    });

    it("drops unsupported clip audio without dropping supported audio reuse", () => {
        const catalog = icLoraOnlyCatalog();
        catalog.architectures[0].capabilities.clip = ["audio-reuse"];
        catalog.architectures[0].capabilities.audioSourceKinds = ["Disabled"];
        const clip = minimalClip({
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            reuseAudio: true,
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion?.clip.audioSource).toBe("Native");
        expect(conversion?.clip.uploadedAudio).toBeNull();
        expect(conversion?.clip.reuseAudio).toBe(true);
        expect(conversion?.removals).toContain("clip audio source settings");
        expect(conversion?.removals).not.toContain(
            "captured stage audio reuse",
        );
    });

    it("drops audio-derived duration without dropping a supported uploaded source", () => {
        const catalog = icLoraOnlyCatalog();
        catalog.architectures[0].capabilities.audioSourceKinds = ["Upload"];
        const clip = minimalClip({
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            clipLengthFromAudio: true,
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion?.clip.audioSource).toBe("Upload");
        expect(conversion?.clip.uploadedAudio).toEqual(clip.uploadedAudio);
        expect(conversion?.clip.clipLengthFromAudio).toBe(false);
        expect(conversion?.removals).toContain("audio-derived clip duration");
        expect(conversion?.removals).not.toContain(
            "clip audio source settings",
        );
    });

    it("drops control-signal duration only when its dedicated capability is absent", () => {
        const catalog = icLoraOnlyCatalog();
        const clip = minimalClip({
            clipLengthFromControlNet: true,
            icLoras: [
                hdrIcLoraFixture({
                    hdr: false,
                    driveSource: "ControlNet 1",
                }),
            ],
            stages: [minimalStage({ icLoraStrengths: [0.4] })],
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion?.clip.clipLengthFromControlNet).toBe(false);
        expect(conversion?.clip.icLoras).toHaveLength(1);
        expect(conversion?.clip.stages[0].icLoraStrengths).toEqual([0.4]);
        expect(conversion?.removals).toContain(
            "control-signal-derived clip duration",
        );
    });

    it("retains supported control-signal duration when unrelated LTX features are removed", () => {
        const catalog = testArchitectureCatalog();
        catalog.architectures[0].capabilities = testArchitectureCapabilities({
            clip: ["control-signal-derived-duration"],
            stage: ["ic-lora"],
            upscaleModes: [],
            audioSourceKinds: ["Disabled"],
        });
        const clip = minimalClip({
            audioSource: "Upload",
            uploadedAudio: {
                data: "data:audio/wav;base64,AA==",
                fileName: "voice.wav",
            },
            clipLengthFromControlNet: true,
            icLoras: [
                hdrIcLoraFixture({ driveSource: "Upload" }),
                hdrIcLoraFixture({
                    hdr: false,
                    preset: "pose",
                    driveSource: "ControlNet 1",
                }),
            ],
            stages: [minimalStage({ icLoraStrengths: [0.2, 0.4] })],
        });

        const conversion = planArchitectureConversion(
            clip,
            {
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
                model: "ltx",
                capabilities: catalog.architectures[0].capabilities,
            },
            catalog,
        );

        expect(conversion?.clip.audioSource).toBe("Native");
        expect(conversion?.clip.uploadedAudio).toBeNull();
        expect(conversion?.clip.icLoras).toHaveLength(1);
        expect(conversion?.clip.icLoras[0]).toMatchObject({
            hdr: false,
            driveSource: "ControlNet 1",
        });
        expect(conversion?.clip.stages[0].icLoraStrengths).toEqual([0.4]);
        expect(conversion?.clip.clipLengthFromControlNet).toBe(true);
        expect(conversion?.removals).toEqual(
            expect.arrayContaining([
                "1 HDR IC-LoRA",
                "clip audio source settings",
            ]),
        );
        expect(conversion?.removals).not.toContain(
            "control-signal-derived clip duration",
        );
        expect(
            deriveArchitectureDiagnostics(
                conversion ? [conversion.clip] : [],
                catalog,
            ).map(({ code }) => code),
        ).not.toContain("architecture.unusable.clip-length-from-control-net");
    });

    it("attributes duration cleanup to its owner after audio source normalization", () => {
        const catalog = icLoraOnlyCatalog();
        catalog.architectures[0].capabilities.clip = [
            "audio-sources",
            "audio-derived-duration",
        ];
        catalog.architectures[0].capabilities.audioSourceKinds = [
            "Native",
            "Upload",
        ];
        const clip = minimalClip({
            audioSource: "ControlNet",
            clipLengthFromAudio: true,
        });

        const conversion = planArchitectureConversion(
            clip,
            targetFor(catalog),
            catalog,
        );

        expect(conversion?.clip.audioSource).toBe("Native");
        expect(conversion?.clip.uploadedAudio).toBeNull();
        expect(conversion?.clip.clipLengthFromAudio).toBe(false);
        expect(conversion?.removals).toEqual(
            expect.arrayContaining([
                "clip audio source settings",
                "audio-derived clip duration",
            ]),
        );
    });
});
