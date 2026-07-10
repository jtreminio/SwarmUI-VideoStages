import { beforeEach, describe, expect, it } from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
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
import { clearUiStateForTests } from "./uiState";

const dataInput = (): HTMLTextAreaElement =>
    document.getElementById("input_videostages") as HTMLTextAreaElement;
const promptEl = (): HTMLTextAreaElement =>
    document.getElementById("input_prompt") as HTMLTextAreaElement;

describe("persistence", () => {
    describe("serializeClipsForStorage", () => {
        it("serializes only structural clip, ref, and stage fields (no UI/prompt fields)", () => {
            const clips = [
                minimalClip({
                    duration: 3,
                    controlNetSource: "ControlNet 2",
                    controlNetLora: "ltx-ic-lora.safetensors",
                    clipLengthFromControlNet: true,
                    prompt: "should not be serialized",
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
                            source: REF_SOURCE_BASE,
                            uploadFileName: null,
                            uploadedImage: null,
                            frame: 2,
                            fromEnd: true,
                        },
                    ],
                    stages: [
                        {
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
            const serialized = serializeClipsForStorage(clips);
            expect(serialized).toEqual(expected);
            expect(JSON.stringify(serialized)).not.toContain("prompt");
            expect(JSON.stringify(serialized)).not.toContain("hue");
        });
    });

    describe("round-trips through the Data param + prompt box", () => {
        beforeEach(() => {
            __resetPersistenceForTests();
            clearUiStateForTests();
            document.body.innerHTML = "";
            mountVideoStagesData({ clips: [] });
            mountPromptBox("a cinematic shot");
        });

        it("writes structure to the Data param and prompt text to the prompt box", () => {
            saveClips([
                minimalClip({ duration: 3, prompt: "a red fox" }),
                minimalClip({ duration: 4, prompt: "a bear" }),
            ]);

            const stored = JSON.parse(dataInput().value) as {
                clips: { duration: number }[];
            };
            expect(stored.clips.map((c) => c.duration)).toEqual([3, 4]);
            expect(dataInput().value).not.toContain("\\u003c");

            expect(promptEl().value.startsWith("a cinematic shot")).toBe(true);
            expect(promptEl().value).toContain("<videoclip[0]>a red fox");
            expect(promptEl().value).toContain("<videoclip[1]>a bear");
            expect(promptEl().value).not.toContain("<videostages>");

            expect(getClips().map((clip) => clip.duration)).toEqual([3, 4]);
            expect(getClips().map((clip) => clip.prompt)).toEqual([
                "a red fox",
                "a bear",
            ]);
        });

        it("round-trips a prompt window (seconds) through the prompt box", () => {
            saveClips([
                minimalClip({
                    duration: 5,
                    prompt: "base",
                    promptWindows: [{ start: 1, duration: 2, prompt: "gust" }],
                }),
            ]);
            expect(promptEl().value).toContain("<videoclip[0]:1-3>gust");

            const windows = getClips()[0].promptWindows;
            expect(windows).toEqual([
                { start: 1, duration: 2, prompt: "gust" },
            ]);
        });

        it("does not duplicate <videoclip> sections on a subsequent save", () => {
            saveClips([minimalClip({ duration: 2, prompt: "one" })]);
            saveClips([
                minimalClip({ duration: 5, prompt: "five" }),
                minimalClip({ duration: 6, prompt: "six" }),
            ]);
            const value = promptEl().value;
            expect(value.startsWith("a cinematic shot")).toBe(true);
            expect(value.split("<videoclip[0]>").length - 1).toBe(1);
            expect(getClips().map((clip) => clip.prompt)).toEqual([
                "five",
                "six",
            ]);
        });
    });

    describe("top-level width/height/fps round-trip", () => {
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
            clearUiStateForTests();
            document.body.innerHTML = "";
            mountVideoStagesData({ clips: [] });
            mountPromptBox("");
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
            expect(dataInput().value).toContain('"width":640');
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
            expect(state.width).toBe(1024);
            expect(state.height).toBe(1024);
            expect(state.fps).toBe(24);
        });

        it("treats out-of-range stored values as inherit", () => {
            dataInput().value = '{"width":100,"height":100,"fps":0,"clips":[]}';
            const state = getState();
            expect(state.dimsExplicit).toBe(false);
            expect(state.fpsExplicit).toBe(false);
            expect(state.width).toBe(1024);
        });

        it("treats a lone width (no height) as inherit for dims", () => {
            dataInput().value = '{"width":512,"clips":[]}';
            const state = getState();
            expect(state.dimsExplicit).toBe(false);
            expect(state.width).toBe(1024);
        });
    });
});
