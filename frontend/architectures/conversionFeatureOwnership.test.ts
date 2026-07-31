import { describe, expect, it } from "@jest/globals";
import { fakeArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { planArchitectureConversion } from "./conversion/plan";

describe("nondestructive architecture conversion", () => {
    it("retargets models while preserving unsupported authored extras as dormant data", () => {
        const catalog = fakeArchitectureCatalog();
        const clip = minimalClip({
            refs: [minimalRef()],
            promptWindows: [{ prompt: "later", start: 1, duration: 1 }],
            reuseAudio: true,
            clipLengthFromAudio: true,
            loras: [{ name: "detail.safetensors" }],
            icLoras: [
                {
                    lora: "guide.safetensors",
                    preset: "pose",
                    stage: -1,
                    driveSource: "Upload",
                    driveData: "visual",
                    driveMedia: null,
                    driveMediaKinds: ["image", "video"],
                    strength: 1,
                    attentionStrength: 1,
                    controlType: "none",
                    hdr: false,
                },
            ],
            stages: [
                minimalStage({
                    loraWeights: [0.7],
                    icLoraStrengths: [0.4],
                    upscale: 2,
                }),
            ],
        });
        const before = structuredClone(clip);
        const conversion = planArchitectureConversion(
            clip,
            {
                architectureId: "test-video",
                modelProfileId: "stale-profile-hint",
                model: "test-video.safetensors",
                capabilities: catalog.architectures[0].capabilities,
                entryModes: ["text-to-video", "image-to-video"],
            },
            catalog,
        );

        expect(conversion).not.toBeNull();
        expect(conversion?.removals).toEqual([]);
        expect(conversion?.clip).toMatchObject({
            refs: before.refs,
            promptWindows: before.promptWindows,
            reuseAudio: true,
            clipLengthFromAudio: true,
            loras: before.loras,
            icLoras: before.icLoras,
        });
        expect(conversion?.clip.stages[0]).toMatchObject({
            model: "test-video.safetensors",
            modelProfileId: "test-profile",
            loraWeights: [0.7],
            icLoraStrengths: [0.4],
            upscale: 2,
        });
        expect(clip).toEqual(before);
    });
});
