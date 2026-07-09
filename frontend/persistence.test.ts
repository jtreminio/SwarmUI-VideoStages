import { beforeEach, describe, expect, it } from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import {
    __resetPersistenceForTests,
    getClips,
    getState,
    saveClips,
    saveState,
    serializeClipsForStorage,
    serializeStateForStorage,
} from "./persistence";
import {
    REF_SOURCE_BASE,
    type StoredClip,
    type VideoStagesConfig,
} from "./types";

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

    describe("top-level width/height/fps round-trip", () => {
        const promptEl = (): HTMLTextAreaElement =>
            document.getElementById("input_prompt") as HTMLTextAreaElement;
        const section = (): string => {
            const value = promptEl().value;
            const at = value.indexOf("<videostages>");
            return value.slice(at + "<videostages>".length);
        };
        const baseState = (
            overrides: Partial<VideoStagesConfig> = {},
        ): VideoStagesConfig => ({
            width: 1024,
            height: 1024,
            fps: 24,
            dimsExplicit: false,
            fpsExplicit: false,
            clips: [minimalClip({ duration: 2 })],
            ...overrides,
        });

        beforeEach(() => {
            __resetPersistenceForTests();
            const prompt = document.createElement("textarea");
            prompt.id = "input_prompt";
            prompt.value = "";
            document.body.appendChild(prompt);
        });

        it("omits width/height/fps when they are inherited", () => {
            const json = serializeStateForStorage(baseState());
            const parsed = JSON.parse(json) as Record<string, unknown>;
            expect("width" in parsed).toBe(false);
            expect("height" in parsed).toBe(false);
            expect("fps" in parsed).toBe(false);
        });

        it("includes width+height together only when dims are explicit", () => {
            const json = serializeStateForStorage(
                baseState({ width: 512, height: 768, dimsExplicit: true }),
            );
            const parsed = JSON.parse(json) as Record<string, unknown>;
            expect(parsed.width).toBe(512);
            expect(parsed.height).toBe(768);
            expect("fps" in parsed).toBe(false);
        });

        it("includes fps only when fps is explicit", () => {
            const json = serializeStateForStorage(
                baseState({ fps: 30, fpsExplicit: true }),
            );
            const parsed = JSON.parse(json) as Record<string, unknown>;
            expect(parsed.fps).toBe(30);
            expect("width" in parsed).toBe(false);
        });

        it("round-trips explicit dims + fps through getState", () => {
            saveState(
                baseState({
                    width: 640,
                    height: 384,
                    dimsExplicit: true,
                    fps: 16,
                    fpsExplicit: true,
                }),
            );
            expect(section()).toContain('"width":640');
            const state = getState();
            expect(state.dimsExplicit).toBe(true);
            expect(state.width).toBe(640);
            expect(state.height).toBe(384);
            expect(state.fpsExplicit).toBe(true);
            expect(state.fps).toBe(16);
        });

        it("treats omitted keys as inherit, falling back to root defaults", () => {
            saveState(baseState());
            const state = getState();
            expect(state.dimsExplicit).toBe(false);
            expect(state.fpsExplicit).toBe(false);
            // No core inputs mounted → getRootDefaults() defaults.
            expect(state.width).toBe(1024);
            expect(state.height).toBe(1024);
            expect(state.fps).toBe(24);
        });

        it("treats out-of-range stored values as inherit", () => {
            promptEl().value =
                '<videostages>{"width":100,"height":100,"fps":0,"clips":[]}';
            const state = getState();
            expect(state.dimsExplicit).toBe(false);
            expect(state.fpsExplicit).toBe(false);
            expect(state.width).toBe(1024);
        });

        it("treats a lone width (no height) as inherit for dims", () => {
            promptEl().value = '<videostages>{"width":512,"clips":[]}';
            const state = getState();
            expect(state.dimsExplicit).toBe(false);
            expect(state.width).toBe(1024);
        });
    });
});
