import { afterEach, beforeEach, describe, expect, it } from "@jest/globals";
import { resetArchitectureCatalogForTests } from "./__test_helpers__/architectureCatalog";
import { testArchitectureCatalogDto } from "./__test_helpers__/architectureFixtures";
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
import { loadAuthoritativeArchitectureCatalog } from "./architectures/catalog";
import { defaultIcLora } from "./architectures/ltx2/icLoraNormalization";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import {
    collectAuthoringEntityIds,
    ensureAuthoringDocumentIdentity,
} from "./identity";
import { serializeStateForStorage } from "./persistence/documentCodec";
import {
    __resetPersistenceForTests,
    getState,
    saveState,
} from "./persistence/repository";
import {
    CURRENT_AUTHORING_SCHEMA_VERSION,
    type VideoStagesConfig,
} from "./types";
import { clearUiStateForTests } from "./uiState";

const baseState = (
    clips: VideoStagesConfig["clips"],
    overrides: Partial<VideoStagesConfig> = {},
): VideoStagesConfig => ({
    width: 1024,
    height: 1024,
    fps: 24,
    dimsExplicit: false,
    clips,
    ...overrides,
});

const dataJson = (): Record<string, unknown> =>
    JSON.parse(
        (document.getElementById("input_videostages") as HTMLTextAreaElement)
            .value,
    ) as Record<string, unknown>;

const mountRootDefaults = (): void => {
    mountSelect("input_videomodel", {
        value: "ltx-2.3.safetensors",
        options: ["ltx-2.3.safetensors"],
    });
    mountSelect("input_loras", { options: [] });
    mountSelect("input_sampler", { options: ["euler"] });
    mountSelect("input_scheduler", { options: ["normal"] });
    mountSelect("input_refinerupscalemethod", { options: ["pixel-lanczos"] });
};

describe("versioned authoring document identity", () => {
    beforeEach(async () => {
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => testArchitectureCatalogDto(),
        });
        await loadAuthoritativeArchitectureCatalog();
        __resetPersistenceForTests();
        clearUiStateForTests();
        document.body.innerHTML = "";
        mountRootDefaults();
        mountVideoStagesData({ clips: [] });
        mountPromptBox("");
    });

    afterEach(() => {
        resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
    });

    it.each([
        ["legacy array", [{ duration: 5, stages: [{ model: "m" }] }]],
        [
            "schema v2 document",
            {
                schemaVersion: 2,
                clips: [{ duration: 5, stages: [{ model: "m" }] }],
            },
        ],
    ])("rejects a %s instead of silently migrating it", (_label, carrier) => {
        mountVideoStagesData(carrier);
        mountPromptBox("<videoclip[0]>must not be attached");

        const state = getState();

        expect(state.schemaVersion).toBe(CURRENT_AUTHORING_SCHEMA_VERSION);
        expect(state.clips).toEqual([]);
        expect(state.audioTracks).toEqual([]);
        expect(collectAuthoringEntityIds(state)).toEqual([]);
    });

    it("derives the same missing v3 IDs across a prompt-only cache invalidation before save", () => {
        mountVideoStagesData({
            schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
            clips: [
                {
                    duration: 3,
                    refs: [{ source: "Base", frame: 1 }],
                    stages: [
                        {
                            model: "ltx-2.3.safetensors",
                            modelProfileId: "ltx-2.3",
                        },
                    ],
                },
            ],
        });
        const prompt = mountPromptBox(
            "<videoclip[0]>first\n<videoclip[0]:0-1>detail one",
        );
        const before = collectAuthoringEntityIds(getState());

        prompt.value = "<videoclip[0]>second\n<videoclip[0]:0-1>detail two";
        const after = collectAuthoringEntityIds(getState());

        expect(after).toEqual(before);
        expect(after).toEqual([
            "clip_legacy_0",
            "stage_legacy_0_0",
            "ref_legacy_0_0",
            "prompt_window_legacy_0_0",
        ]);
    });

    it("durably canonicalizes whitespace and globally duplicate v3 carrier IDs on a no-op save", () => {
        mountVideoStagesData({
            schemaVersion: CURRENT_AUTHORING_SCHEMA_VERSION,
            clips: [
                {
                    id: " clip-a ",
                    duration: 3,
                    stages: [
                        {
                            id: "duplicate",
                            model: "ltx-2.3.safetensors",
                            modelProfileId: "ltx-2.3",
                        },
                    ],
                    refs: [{ id: "duplicate", source: "Base", frame: 1 }],
                    icLoras: [
                        defaultIcLora({
                            id: "duplicate",
                            lora: "guide.safetensors",
                        }),
                    ],
                    retake: null,
                },
            ],
            audioTracks: [],
        });
        const repaired = getState();
        const repairedIds = collectAuthoringEntityIds(repaired);
        expect(repaired.clips[0].id).toBe("clip-a");
        expect(new Set(repairedIds).size).toBe(repairedIds.length);

        saveState(repaired, { notifyDomChange: false });

        const stored = dataJson() as {
            clips: {
                id: string;
                stages: { id: string }[];
                refs: { id: string }[];
                icLoras: { id: string }[];
            }[];
        };
        const storedIds = [
            stored.clips[0].id,
            stored.clips[0].stages[0].id,
            stored.clips[0].refs[0].id,
            stored.clips[0].icLoras[0].id,
        ];
        expect(storedIds).toEqual([
            repaired.clips[0].id,
            repaired.clips[0].stages[0].id,
            repaired.clips[0].refs[0].id,
            repaired.clips[0].icLoras[0].id,
        ]);
        expect(storedIds.every((id) => id.trim() === id)).toBe(true);
        expect(new Set(storedIds).size).toBe(storedIds.length);
    });

    it("repairs missing and duplicate IDs across every entity namespace", () => {
        const duplicate = "same";
        const state = baseState(
            [
                minimalClip({
                    id: duplicate,
                    stages: [minimalStage({ id: duplicate })],
                    refs: [minimalRef({ id: duplicate })],
                    icLoras: [
                        defaultIcLora({
                            id: duplicate,
                            lora: "guide.safetensors",
                        }),
                    ],
                    promptWindows: [
                        {
                            id: duplicate,
                            prompt: "window",
                            start: 0,
                            duration: 1,
                        },
                    ],
                    retake: {
                        id: duplicate,
                        startSeconds: 0,
                        lengthSeconds: 1,
                        strength: 1,
                    },
                }),
            ],
            {
                audioTracks: [
                    {
                        id: duplicate,
                        source: {
                            kind: "External",
                            reference: "music",
                            uploadedAudio: null,
                        },
                        spans: [
                            {
                                id: duplicate,
                                timelineStartSeconds: null,
                                timelineLengthSeconds: null,
                                sourceStartSeconds: 0,
                            },
                        ],
                    },
                ],
            },
        );

        ensureAuthoringDocumentIdentity(state);
        const ids = collectAuthoringEntityIds(state);
        expect(ids).toHaveLength(8);
        expect(new Set(ids).size).toBe(ids.length);
        expect(state.clips[0].id).toBe(duplicate);
        expect(state.schemaVersion).toBe(CURRENT_AUTHORING_SCHEMA_VERSION);
    });

    it("reserves later stable IDs before assigning missing clip and nested IDs", () => {
        const stableClip = minimalClip({
            id: "clip_legacy_0",
            stages: [minimalStage({ id: "stage-stable" })],
        });
        const nestedClip = minimalClip({
            id: "clip-nested",
            stages: [minimalStage(), minimalStage({ id: "stage_legacy_1_0" })],
            refs: [minimalRef(), minimalRef({ id: "ref_legacy_1_0" })],
            promptWindows: [
                { prompt: "new", start: 0, duration: 0.5 },
                {
                    id: "prompt_window_legacy_1_0",
                    prompt: "stable",
                    start: 1,
                    duration: 0.5,
                },
            ],
        });
        const missingClip = minimalClip({
            stages: [minimalStage({ id: "stage-new-clip" })],
        });
        const stableStage = nestedClip.stages[1];
        const stableRef = nestedClip.refs[1];
        const stableWindow = nestedClip.promptWindows[1];
        const stableSpan = {
            id: "audio_span_legacy_0_0",
            timelineStartSeconds: null,
            timelineLengthSeconds: null,
            sourceStartSeconds: 0,
        };
        const state = baseState([missingClip, nestedClip, stableClip], {
            audioTracks: [
                {
                    id: "track-fixed",
                    source: {
                        kind: "External",
                        reference: "music",
                        uploadedAudio: null,
                    },
                    spans: [
                        {
                            timelineStartSeconds: 0,
                            timelineLengthSeconds: 1,
                            sourceStartSeconds: 0,
                        },
                        stableSpan,
                    ],
                },
            ],
        });

        ensureAuthoringDocumentIdentity(state);

        expect(stableClip.id).toBe("clip_legacy_0");
        expect(missingClip.id).not.toBe(stableClip.id);
        expect(stableStage.id).toBe("stage_legacy_1_0");
        expect(nestedClip.stages[0].id).not.toBe(stableStage.id);
        expect(stableRef.id).toBe("ref_legacy_1_0");
        expect(nestedClip.refs[0].id).not.toBe(stableRef.id);
        expect(stableWindow.id).toBe("prompt_window_legacy_1_0");
        expect(nestedClip.promptWindows[0].id).not.toBe(stableWindow.id);
        expect(stableSpan.id).toBe("audio_span_legacy_0_0");
        expect(state.audioTracks?.[0].spans[0].id).not.toBe(stableSpan.id);
        expect(new Set(collectAuthoringEntityIds(state)).size).toBe(
            collectAuthoringEntityIds(state).length,
        );
    });

    it("preserves clip, hue, and prompt-window identity after clip reorder", () => {
        const state = baseState([
            minimalClip({
                id: "clip-a",
                hue: 10,
                prompt: "a",
                promptWindows: [
                    {
                        id: "window-a",
                        prompt: "detail-a",
                        start: 0,
                        duration: 1,
                    },
                ],
            }),
            minimalClip({
                id: "clip-b",
                hue: 220,
                prompt: "b",
                promptWindows: [
                    {
                        id: "window-b",
                        prompt: "detail-b",
                        start: 1,
                        duration: 1,
                    },
                ],
            }),
        ]);
        saveState(state, { notifyDomChange: false });

        const reordered = getState();
        reordered.clips.reverse();
        saveState(reordered, { notifyDomChange: false });
        __resetPersistenceForTests();

        const reloaded = getState();
        expect(reloaded.clips.map((clip) => clip.id)).toEqual([
            "clip-b",
            "clip-a",
        ]);
        expect(reloaded.clips.map((clip) => clip.hue)).toEqual([220, 10]);
        expect(reloaded.clips.map((clip) => clip.promptWindows[0].id)).toEqual([
            "window-b",
            "window-a",
        ]);
    });

    it("keeps nested IDs attached when stored collections are reordered", () => {
        const state = baseState(
            [
                minimalClip({
                    id: "clip-nested",
                    refs: [
                        minimalRef({ id: "ref-a", frame: 1 }),
                        minimalRef({ id: "ref-b", frame: 2 }),
                    ],
                    stages: [
                        minimalStage({ id: "stage-a" }),
                        minimalStage({ id: "stage-b" }),
                    ],
                }),
            ],
            {
                audioTracks: [
                    {
                        id: "track-a",
                        source: {
                            kind: "External",
                            reference: "a",
                            uploadedAudio: null,
                        },
                        spans: [
                            {
                                id: "span-a",
                                timelineStartSeconds: null,
                                timelineLengthSeconds: null,
                                sourceStartSeconds: 0,
                            },
                        ],
                    },
                    {
                        id: "track-b",
                        source: {
                            kind: "External",
                            reference: "b",
                            uploadedAudio: null,
                        },
                        spans: [
                            {
                                id: "span-b",
                                timelineStartSeconds: 1,
                                timelineLengthSeconds: 0.5,
                                sourceStartSeconds: 0,
                            },
                        ],
                    },
                ],
            },
        );
        saveState(state, { notifyDomChange: false });

        const reordered = getState();
        const clip = reordered.clips[0];
        clip.refs.reverse();
        clip.stages.reverse();
        reordered.audioTracks?.reverse();
        saveState(reordered, { notifyDomChange: false });
        __resetPersistenceForTests();

        const reloaded = getState();
        expect(reloaded.clips[0].refs.map((ref) => ref.id)).toEqual([
            "ref-b",
            "ref-a",
        ]);
        expect(reloaded.clips[0].stages.map((stage) => stage.id)).toEqual([
            "stage-b",
            "stage-a",
        ]);
        expect(
            reloaded.audioTracks?.map((track) => [track.id, track.spans[0].id]),
        ).toEqual([
            ["track-b", "span-b"],
            ["track-a", "span-a"],
        ]);
    });

    it("round-trips a one-clip relative root audio span", () => {
        const state = baseState([minimalClip({ id: "clip-one" })], {
            audioTracks: [
                {
                    id: "track-voice",
                    source: {
                        kind: "Upload",
                        reference: "voice.wav",
                        uploadedAudio: {
                            data: "data:audio/wav;base64,AAAA",
                            fileName: "voice.wav",
                        },
                    },
                    spans: [
                        {
                            id: "span-voice",
                            timelineStartSeconds: null,
                            timelineLengthSeconds: null,
                            sourceStartSeconds: 0.5,
                        },
                    ],
                },
            ],
        });

        const serialized = serializeStateForStorage(state);
        mountVideoStagesData(serialized);
        __resetPersistenceForTests();
        expect(getState().audioTracks).toEqual(state.audioTracks);
    });

    it("round-trips a multi-clip root audio span with an optional timeline window", () => {
        const state = baseState(
            [
                minimalClip({ id: "clip-first" }),
                minimalClip({ id: "clip-last" }),
            ],
            {
                audioTracks: [
                    {
                        id: "track-score",
                        source: {
                            kind: "AceStepFun",
                            reference: "audio1",
                            uploadedAudio: null,
                        },
                        spans: [
                            {
                                id: "span-score",
                                timelineStartSeconds: 0.5,
                                timelineLengthSeconds: 3,
                                sourceStartSeconds: 4,
                            },
                        ],
                    },
                ],
            },
        );

        saveState(state, { notifyDomChange: false });
        __resetPersistenceForTests();
        const reloaded = getState();
        expect(reloaded.audioTracks).toEqual(state.audioTracks);
        expect(reloaded.audioTracks?.[0].spans[0]).toMatchObject({
            timelineStartSeconds: 0.5,
            timelineLengthSeconds: 3,
        });
    });
});
