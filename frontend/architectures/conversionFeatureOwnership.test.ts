import { describe, expect, it } from "@jest/globals";
import { fakeArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "../__test_helpers__/clipFixtures";
import { planArchitectureConversion } from "./conversion/plan";

describe("architecture conversion feature ownership", () => {
    it("retargets models and discards features the target cannot use", () => {
        const catalog = fakeArchitectureCatalog();
        const clip = minimalClip({
            frameRefs: [minimalRef()],
            promptWindows: [{ prompt: "later", start: 1, duration: 1 }],
            reuseAudio: true,
            clipLengthFromAudio: true,
            loras: [{ name: "detail.safetensors", weight: 0.7 }],
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
                },
            ],
            stages: [
                minimalStage({
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
            },
            catalog,
        );

        expect(conversion).not.toBeNull();
        expect(conversion).toMatchObject({
            frameRefs: [],
            promptWindows: [],
            reuseAudio: false,
            clipLengthFromAudio: false,
            loras: before.loras,
            icLoras: [],
        });
        expect(conversion?.stages[0]).toMatchObject({
            model: "test-video.safetensors",
            modelProfileId: "test-profile",
            icLoraStrengths: [],
            upscale: 1,
        });
        expect(clip).toEqual(before);
    });
});
