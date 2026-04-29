import { describe, expect, it } from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import { serializeClipsForStorage } from "./persistence";
import { REF_SOURCE_BASE, type StoredClip } from "./types";

describe("persistence", () => {
    describe("serializeClipsForStorage", () => {
        it("serializes only persisted clip, ref, and stage fields for storage", () => {
            const clips = [
                minimalClip({
                    duration: 3,
                    controlNetSource: "ControlNet 2",
                    controlNetLora: "ltx-ic-lora.safetensors",
                    clipLengthFromControlNet: true,
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
            const expected: StoredClip[] = [
                {
                    expanded: true,
                    skipped: false,
                    duration: 3,
                    audioSource: "Native",
                    controlNetSource: "ControlNet 2",
                    controlNetLora: "ltx-ic-lora.safetensors",
                    saveAudioTrack: false,
                    clipLengthFromAudio: false,
                    clipLengthFromControlNet: true,
                    reuseAudio: false,
                    uploadedAudio: null,
                    refs: [
                        {
                            expanded: true,
                            source: REF_SOURCE_BASE,
                            uploadFileName: null,
                            uploadedImage: null,
                            frame: 2,
                            fromEnd: true,
                        },
                    ],
                    stages: [
                        {
                            expanded: true,
                            skipped: false,
                            control: 1,
                            controlNetStrength: 0.7,
                            refStrengths: [0.8],
                            upscale: 1,
                            upscaleMethod: "pixel-lanczos",
                            model: "m",
                            vae: "v",
                            steps: 8,
                            cfgScale: 1,
                            sampler: "euler",
                            scheduler: "normal",
                        },
                    ],
                },
            ];
            expect(serializeClipsForStorage(clips)).toEqual(expected);
        });
    });
});
