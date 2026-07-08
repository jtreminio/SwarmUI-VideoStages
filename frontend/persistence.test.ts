import { beforeEach, describe, expect, it } from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import {
    __resetPersistenceForTests,
    getClips,
    saveClips,
    serializeClipsForStorage,
} from "./persistence";
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
                        }),
                    ],
                }),
            ];
            const expected: StoredClip[] = [
                {
                    expanded: true,
                    skipped: false,
                    hue: 210,
                    duration: 3,
                    audioSource: "Native",
                    controlNetSource: "ControlNet 2",
                    controlNetLora: "ltx-ic-lora.safetensors",
                    saveAudioTrack: false,
                    clipLengthFromAudio: false,
                    clipLengthFromControlNet: true,
                    reuseAudio: false,
                    uploadedAudio: null,
                    prompt: "",
                    negativePrompt: "",
                    promptWindows: [],
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
                            upscaleMethod: "latentmodel-test.safetensors",
                            model: "m",
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

    describe("round-trips through the <videostages> section of #input_prompt", () => {
        const promptEl = (): HTMLTextAreaElement =>
            document.getElementById("input_prompt") as HTMLTextAreaElement;

        beforeEach(() => {
            __resetPersistenceForTests();
            const prompt = document.createElement("textarea");
            prompt.id = "input_prompt";
            prompt.value = "a cinematic shot";
            document.body.appendChild(prompt);
        });

        it("writes the config into the section, preserving surrounding prompt text, and reads it back", () => {
            saveClips([
                minimalClip({ duration: 3 }),
                minimalClip({ duration: 4 }),
            ]);

            expect(promptEl().value.startsWith("a cinematic shot")).toBe(true);
            expect(promptEl().value).toContain("<videostages>");
            expect(getClips().map((clip) => clip.duration)).toEqual([3, 4]);
        });

        it("replaces only the section body on a subsequent save, leaving prose intact", () => {
            saveClips([minimalClip({ duration: 2 })]);
            saveClips([
                minimalClip({ duration: 5 }),
                minimalClip({ duration: 6 }),
            ]);

            const value = promptEl().value;
            expect(value.startsWith("a cinematic shot")).toBe(true);
            // Exactly one section — the write replaces the old body rather than appending a second opener.
            expect(value.split("<videostages>").length - 1).toBe(1);
            expect(getClips().map((clip) => clip.duration)).toEqual([5, 6]);
        });
    });
});
