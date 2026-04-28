import { describe, expect, it } from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import { serializeClipsForStorage } from "./persistence";

describe("persistence", () => {
    describe("serializeClipsForStorage", () => {
        it("copies persisted clip, ref, and stage fields for storage", () => {
            const clips = [
                minimalClip({
                    duration: 3,
                    controlNetSource: "ControlNet 2",
                    controlNetLora: "ltx-ic-lora.safetensors",
                    refs: [minimalRef({ frame: 2, fromEnd: true })],
                    stages: [
                        minimalStage({
                            controlNetStrength: 0.7,
                            refStrengths: [0.8],
                            vae: "v",
                        }),
                    ],
                }),
            ];
            expect(serializeClipsForStorage(clips)).toEqual(clips);
        });
    });
});
