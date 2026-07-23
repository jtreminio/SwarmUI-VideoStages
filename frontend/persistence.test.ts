import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import {
    __resetPersistenceForTests,
    dispatchDocumentCommand,
    getClips,
    getState,
    getTimelineStore,
    saveClips,
    saveState,
    serializeClipsForStorage,
    serializeStateForStorage,
} from "./persistence";
import { decodeStoredDocument } from "./persistence/documentCodec";
import type { StoredClip } from "./storageTypes";
import { REF_SOURCE_BASE, type VideoStagesConfig } from "./types";
import { clearUiStateForTests } from "./uiState";

const dataInput = (): HTMLTextAreaElement =>
    document.getElementById("input_videostages") as HTMLTextAreaElement;
const promptEl = (): HTMLTextAreaElement =>
    document.getElementById("input_prompt") as HTMLTextAreaElement;

describe("persistence", () => {
    afterEach(() => {
        jest.restoreAllMocks();
    });

    describe("strict v3 collection decoding", () => {
        const decode = (document: unknown) =>
            decodeStoredDocument(JSON.stringify(document), {
                width: 1024,
                height: 1024,
                fps: 24,
            });

        it.each([
            ["missing clips", { schemaVersion: 3 }],
            ["non-array clips", { schemaVersion: 3, clips: {} }],
            ["null clip item", { schemaVersion: 3, clips: [null] }],
            ["scalar clip item", { schemaVersion: 3, clips: ["clip"] }],
        ])("rejects %s", (_label, document) => {
            expect(decode(document)).toBeNull();
        });

        it.each([
            ["root audioTracks", { audioTracks: {} }],
            ["root audioTracks item", { audioTracks: [null] }],
            ["clip stages", { clips: [{ stages: {} }] }],
            ["clip stage item", { clips: [{ stages: [null] }] }],
            ["clip refs", { clips: [{ refs: {} }] }],
            ["clip ref item", { clips: [{ refs: [1] }] }],
            ["clip audioSegments", { clips: [{ audioSegments: {} }] }],
            [
                "clip audio-segment item",
                { clips: [{ audioSegments: ["segment"] }] },
            ],
            ["clip icLoras", { clips: [{ icLoras: {} }] }],
            ["clip IC-LoRA item", { clips: [{ icLoras: [false] }] }],
            ["stage LoRAs", { clips: [{ stages: [{ loras: {} }] }] }],
            ["stage LoRA item", { clips: [{ stages: [{ loras: [null] }] }] }],
            [
                "stage ref strengths",
                { clips: [{ stages: [{ refStrengths: {} }] }] },
            ],
            [
                "stage ref-strength item",
                { clips: [{ stages: [{ refStrengths: [null] }] }] },
            ],
            ["audio-track spans", { audioTracks: [{ spans: {} }] }],
            ["audio-track span item", { audioTracks: [{ spans: [null] }] }],
            ["audio-track source", { audioTracks: [{ source: "upload" }] }],
        ])("rejects a malformed present %s collection", (_label, partial) => {
            expect(
                decode({
                    schemaVersion: 3,
                    clips: [],
                    ...partial,
                }),
            ).toBeNull();
        });

        it("accepts omitted optional collections without inventing stored items", () => {
            const decoded = decode({ schemaVersion: 3, clips: [{}] });

            expect(decoded).not.toBeNull();
            expect(decoded?.clips).toHaveLength(1);
            expect(decoded?.clips[0]).toMatchObject({
                stages: [],
                refs: [],
                audioSegments: [],
                icLoras: [],
            });
            expect(decoded?.audioTracks).toEqual([]);
        });
    });

    describe("serializeClipsForStorage", () => {
        it("serializes only structural clip, ref, and stage fields (no UI/prompt fields)", () => {
            const clips = [
                minimalClip({
                    duration: 3,
                    icLoras: [
                        {
                            lora: "ltx-ic-lora.safetensors",
                            preset: "custom",
                            source: "ControlNet 2",
                            stage: -1,
                            strength: 1,
                            attentionStrength: 1,
                            controlType: "none",
                            video: null,
                            driveAudioRef: false,
                        },
                    ],
                    clipLengthFromControlNet: true,
                    prompt: "should not be serialized",
                    refs: [minimalRef({ frame: 2, fromEnd: true })],
                    stages: [
                        minimalStage({
                            controlNetStrength: 0.7,
                            refStrengths: [0.8],
                            loras: [
                                { name: "detail.safetensors", weight: 0.6 },
                            ],
                        }),
                    ],
                }),
            ];
            const serialized = serializeClipsForStorage(clips);
            const expected: StoredClip[] = [
                {
                    id: clips[0].id as string,
                    architecture: "ltx2",
                    modelProfileId: "ltx-2.3",
                    skipped: false,
                    boundaryOut: "cut",
                    boundaryOutCarryAudio: false,
                    boundaryOutOverlap: 8,
                    duration: 3,
                    audioSource: "Native",
                    icLoras: [
                        {
                            lora: "ltx-ic-lora.safetensors",
                            preset: "custom",
                            source: "ControlNet 2",
                            stage: -1,
                            strength: 1,
                            attentionStrength: 1,
                            controlType: "none",
                            video: null,
                            driveAudioRef: false,
                        },
                    ],
                    saveAudioTrack: false,
                    clipLengthFromAudio: false,
                    clipLengthFromControlNet: true,
                    reuseAudio: false,
                    uploadedAudio: null,
                    sourceVideo: null,
                    audioSegments: [],
                    retake: null,
                    refs: [
                        {
                            id: clips[0].refs[0].id as string,
                            source: REF_SOURCE_BASE,
                            uploadFileName: null,
                            uploadedImage: null,
                            frame: 2,
                            fromEnd: true,
                        },
                    ],
                    stages: [
                        {
                            id: clips[0].stages[0].id as string,
                            skipped: false,
                            control: 1,
                            controlNetStrength: 0.7,
                            refStrengths: [0.8],
                            upscale: 1,
                            upscaleMethod: "latentmodel-test.safetensors",
                            model: "ltx-2.3.safetensors",
                            modelProfileId: "ltx-2.3",
                            steps: 8,
                            cfgScale: 1,
                            sampler: "euler",
                            scheduler: "normal",
                            loras: [
                                { name: "detail.safetensors", weight: 0.6 },
                            ],
                        },
                    ],
                },
            ];
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
            mountSelect("input_videomodel", {
                value: "ltx-2.3.safetensors",
                options: ["ltx-2.3.safetensors"],
            });
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

        it("dispatches stable-ID commands through the repository facade", () => {
            saveClips([minimalClip({ duration: 3, prompt: "a red fox" })], {
                notifyDomChange: false,
            });
            const snapshot = getTimelineStore().getSnapshot();
            const clipId = snapshot.state.clips[0].id as string;

            const result = dispatchDocumentCommand(
                {
                    type: "clip.patch",
                    clipId,
                    patch: { duration: 7 },
                },
                {
                    origin: "timeline",
                    notifyDomChange: false,
                    expectedRevision: snapshot.revision,
                },
            );

            expect(result).toEqual({
                applied: true,
                revision: snapshot.revision + 1,
                impacts: ["value", "capabilities"],
            });
            expect(getState().clips[0]).toMatchObject({
                id: clipId,
                duration: 7,
                prompt: "a red fox",
            });
            expect(
                (JSON.parse(dataInput().value) as { clips: { id: string }[] })
                    .clips[0].id,
            ).toBe(clipId);
        });

        it("rejects whole-document attempts to bypass named architecture conversion", () => {
            saveClips([minimalClip()], { notifyDomChange: false });
            const clips = getClips();
            clips[0].architecture = "forged-architecture";
            clips[0].modelProfileId = "forged-profile";
            const error = jest
                .spyOn(console, "error")
                .mockImplementation(() => {});

            expect(() => saveClips(clips, { notifyDomChange: false })).toThrow(
                "architecture-invariant",
            );
            expect(error).toHaveBeenCalled();
            expect(getClips()[0]).toMatchObject({
                architecture: "ltx2",
                modelProfileId: "ltx-2.3",
            });
        });

        it("routes same-profile model edits through invariant-aware retargeting", () => {
            saveClips([minimalClip()], { notifyDomChange: false });
            const model = document.createElement("option");
            model.value = "ltx-2.3-alt.safetensors";
            model.text = "LTX Alt";
            (
                document.getElementById("input_videomodel") as HTMLSelectElement
            ).appendChild(model);
            const clips = getClips();
            clips[0].stages[0].model = model.value;

            expect(() =>
                saveClips(clips, { notifyDomChange: false }),
            ).not.toThrow();
            expect(getClips()[0].stages[0].model).toBe(model.value);
        });

        it("preserves unknown v3 architecture identities for diagnostics and repair", () => {
            mountVideoStagesData({
                schemaVersion: 3,
                clips: [
                    {
                        architecture: "removed-architecture",
                        modelProfileId: "removed-profile",
                        duration: 3,
                        stages: [
                            {
                                model: "removed-model.safetensors",
                                modelProfileId: "removed-stage-profile",
                            },
                        ],
                    },
                ],
            });
            __resetPersistenceForTests();

            const clip = getState().clips[0];

            expect(clip).toMatchObject({
                architecture: "removed-architecture",
                modelProfileId: "removed-profile",
                stages: [
                    {
                        model: "removed-model.safetensors",
                        modelProfileId: "removed-stage-profile",
                    },
                ],
            });
        });

        it("reduces a clip-array save to one atomic batch and one notification", () => {
            const clips = [minimalClip({ duration: 3, prompt: "a red fox" })];
            const callerSnapshot = structuredClone(clips);
            const store = getTimelineStore();
            const dispatchSpy = jest.spyOn(store, "dispatch");
            const subscriber = jest.fn();
            store.subscribe(subscriber);
            const dataChange = jest.fn();
            const promptChange = jest.fn();
            dataInput().addEventListener("change", dataChange);
            promptEl().addEventListener("change", promptChange);

            saveClips(clips, {
                origin: "detail-strip",
                notifyDomChange: true,
                valueOnly: true,
            });

            expect(dispatchSpy).toHaveBeenCalledTimes(1);
            expect(dispatchSpy.mock.calls[0][0]).toMatchObject({
                type: "batch",
                commands: expect.any(Array),
            });
            expect(dispatchSpy.mock.calls[0][3]).toEqual(expect.any(Number));
            expect(subscriber).toHaveBeenCalledTimes(1);
            expect(subscriber.mock.calls[0][1]).toMatchObject({
                origin: "detail-strip",
                hint: "value-only",
            });
            expect(dataChange).toHaveBeenCalledTimes(1);
            expect(promptChange).toHaveBeenCalledTimes(1);
            expect(clips).toEqual(callerSnapshot);
            expect(clips[0].id).toBeUndefined();
            expect(clips[0].stages[0].id).toBeUndefined();
        });

        it("preserves structural order and prompt/UI sidecars through clip-array saves", () => {
            saveClips(
                [
                    minimalClip({
                        id: "clip-a",
                        hue: 15,
                        prompt: "alpha",
                        promptWindows: [
                            {
                                id: "window-a",
                                prompt: "alpha gust",
                                start: 0.5,
                                duration: 1,
                            },
                        ],
                        stages: [minimalStage({ id: "stage-a" })],
                    }),
                    minimalClip({
                        id: "clip-b",
                        hue: 120,
                        prompt: "beta",
                        stages: [minimalStage({ id: "stage-b" })],
                    }),
                    minimalClip({
                        id: "clip-c",
                        hue: 225,
                        prompt: "gamma",
                        stages: [minimalStage({ id: "stage-c" })],
                    }),
                ],
                { notifyDomChange: false },
            );
            const [clipA, , clipC] = getClips();
            const clipD = minimalClip({
                id: "clip-d",
                hue: 300,
                prompt: "delta",
                promptWindows: [
                    {
                        id: "window-d",
                        prompt: "delta rain",
                        start: 1,
                        duration: 0.5,
                    },
                ],
                stages: [minimalStage({ id: "stage-d" })],
            });

            saveClips([clipC, clipD, clipA], {
                notifyDomChange: false,
            });

            const stored = JSON.parse(dataInput().value) as {
                clips: { id: string }[];
            };
            expect(stored.clips.map((clip) => clip.id)).toEqual([
                "clip-c",
                "clip-d",
                "clip-a",
            ]);
            expect(getClips().map((clip) => clip.prompt)).toEqual([
                "gamma",
                "delta",
                "alpha",
            ]);
            expect(promptEl().value).toContain("<videoclip[1]>delta");
            expect(promptEl().value).toContain(
                "<videoclip[1]:1-1.5>delta rain",
            );

            const ui = JSON.parse(
                localStorage.getItem("videostages_ui_state") ?? "{}",
            ) as {
                clips: {
                    id: string;
                    hue: number;
                    promptWindows: { id: string }[];
                }[];
            };
            expect(ui.clips.map(({ id, hue }) => ({ id, hue }))).toEqual([
                { id: "clip-c", hue: 225 },
                { id: "clip-d", hue: 300 },
                { id: "clip-a", hue: 15 },
            ]);
            expect(ui.clips[1].promptWindows[0].id).toBe("window-d");
            expect(ui.clips[2].promptWindows[0].id).toBe("window-a");
        });

        it("dispatches an empty batch for a no-op without writing or notifying", () => {
            saveClips([minimalClip({ duration: 3, prompt: "steady" })], {
                notifyDomChange: false,
            });
            const state = getState();
            const store = getTimelineStore();
            const beforeRevision = store.revision();
            const beforeData = dataInput().value;
            const beforePrompt = promptEl().value;
            const dispatchSpy = jest.spyOn(store, "dispatch");
            const subscriber = jest.fn();
            store.subscribe(subscriber);
            const dataChange = jest.fn();
            const promptChange = jest.fn();
            dataInput().addEventListener("change", dataChange);
            promptEl().addEventListener("change", promptChange);

            saveState(state, {
                origin: "prompt-track",
                notifyDomChange: true,
                valueOnly: true,
            });

            expect(dispatchSpy).toHaveBeenCalledTimes(1);
            expect(dispatchSpy.mock.calls[0][0]).toEqual({
                type: "batch",
                commands: [],
            });
            expect(dispatchSpy.mock.calls[0][3]).toBe(beforeRevision);
            expect(store.revision()).toBe(beforeRevision);
            expect(dataInput().value).toBe(beforeData);
            expect(promptEl().value).toBe(beforePrompt);
            expect(subscriber).not.toHaveBeenCalled();
            expect(dataChange).not.toHaveBeenCalled();
            expect(promptChange).not.toHaveBeenCalled();
        });

        it("rejects a carrier race so a stale compatibility save cannot overwrite it", () => {
            saveClips([minimalClip({ duration: 3, prompt: "original" })], {
                notifyDomChange: false,
            });
            const requested = getState();
            requested.clips[0].duration = 9;
            const store = getTimelineStore();
            const originalDispatch = store.dispatch.bind(store);
            const consoleErrorSpy = jest
                .spyOn(console, "error")
                .mockImplementation(() => {});
            jest.spyOn(store, "dispatch").mockImplementation(
                (command, origin, notifyDomChange, expectedRevision, hint) => {
                    const external = JSON.parse(dataInput().value) as {
                        clips: { duration: number }[];
                    };
                    external.clips[0].duration = 5;
                    dataInput().value = JSON.stringify(external);
                    return originalDispatch(
                        command,
                        origin,
                        notifyDomChange,
                        expectedRevision,
                        hint,
                    );
                },
            );

            expect(() =>
                saveState(requested, { notifyDomChange: false }),
            ).toThrow("stale-revision");
            expect(consoleErrorSpy).toHaveBeenCalledWith(
                "[VideoStages persistence] saveState dispatch failed",
                "stale-revision",
            );
            expect(
                (
                    JSON.parse(dataInput().value) as {
                        clips: { duration: number }[];
                    }
                ).clips[0].duration,
            ).toBe(5);
            expect(getState().clips[0].duration).toBe(5);
        });

        it("round-trips per-clip boundary settings through the Data param", () => {
            saveClips([
                minimalClip({
                    duration: 3,
                    boundaryOut: "crossfade",
                    boundaryOutCarryAudio: true,
                }),
                minimalClip({ duration: 4, boundaryOut: "continue" }),
            ]);
            const stored = JSON.parse(dataInput().value) as {
                clips: {
                    boundaryOut: string;
                    boundaryOutCarryAudio: boolean;
                }[];
            };
            expect(stored.clips.map((c) => c.boundaryOut)).toEqual([
                "crossfade",
                "continue",
            ]);
            expect(stored.clips.map((c) => c.boundaryOutCarryAudio)).toEqual([
                true,
                false,
            ]);
            expect(getClips().map((clip) => clip.boundaryOut)).toEqual([
                "crossfade",
                "continue",
            ]);
            expect(
                getClips().map((clip) => clip.boundaryOutCarryAudio),
            ).toEqual([true, false]);
        });

        it("round-trips a per-clip retake through the Data param", () => {
            saveClips([
                minimalClip({
                    duration: 10,
                    retake: {
                        startSeconds: 2,
                        lengthSeconds: 3,
                        strength: 0.6,
                    },
                }),
            ]);
            const stored = JSON.parse(dataInput().value) as {
                clips: { retake: unknown }[];
            };
            expect(stored.clips[0].retake).toEqual({
                id: expect.any(String),
                startSeconds: 2,
                lengthSeconds: 3,
                strength: 0.6,
            });
            expect(getClips()[0].retake).toEqual({
                id: expect.any(String),
                startSeconds: 2,
                lengthSeconds: 3,
                strength: 0.6,
            });
        });

        it("serializes an absent retake as null and re-parses it as null", () => {
            saveClips([minimalClip({ duration: 4 })]);
            const stored = JSON.parse(dataInput().value) as {
                clips: { retake: unknown }[];
            };
            expect(stored.clips[0].retake).toBeNull();
            expect(getClips()[0].retake).toBeNull();
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
                {
                    id: expect.any(String),
                    start: 1,
                    duration: 2,
                    prompt: "gust",
                },
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
            mountSelect("input_videomodel", {
                value: "ltx-2.3.safetensors",
                options: ["ltx-2.3.safetensors"],
            });
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

        it("normalizes stored refs with an explicit FPS before serializing and reloading", () => {
            saveState(
                baseState({
                    fps: 16,
                    fpsExplicit: true,
                    clips: [
                        minimalClip({
                            duration: 5,
                            refs: [minimalRef({ frame: 120 })],
                        }),
                    ],
                }),
            );

            // 5 seconds at the saved 16fps timeline has an aligned max of 81,
            // not the host's 24fps max of 121.
            __resetPersistenceForTests();
            const reloaded = getState();
            expect(reloaded.fps).toBe(16);
            expect(reloaded.clips[0].refs[0].frame).toBe(81);
            expect(JSON.parse(dataInput().value).fps).toBe(16);
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
