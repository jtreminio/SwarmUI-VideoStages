import { describe, expect, it } from "@jest/globals";
import { reduceDocumentCommand } from "./documentCommands";
import { DocumentDiffError, diffDocuments } from "./documentDiff";
import type {
    CanonicalAudioSegment,
    CanonicalAudioTrack,
    CanonicalAudioTrackSpan,
    CanonicalClip,
    CanonicalPromptWindow,
    CanonicalRefImage,
    CanonicalRetake,
    CanonicalStage,
    CanonicalVideoStagesConfig,
} from "./types";

const stage = (id: string): CanonicalStage => ({
    id,
    skipped: false,
    control: 0.5,
    controlNetStrength: 1,
    refStrengths: [0.5],
    upscale: 1,
    upscaleMethod: "pixel-lanczos",
    model: "ltx",
    steps: 8,
    cfgScale: 1,
    sampler: "euler",
    scheduler: "normal",
    loras: [],
});

const ref = (id: string): CanonicalRefImage => ({
    id,
    source: "Base",
    uploadFileName: null,
    uploadedImage: null,
    frame: 1,
    fromEnd: false,
});

const segment = (id: string): CanonicalAudioSegment => ({
    id,
    source: "audio0",
    startSeconds: 0,
    trimStartSeconds: 0,
    lengthSeconds: 1,
});

const window = (id: string): CanonicalPromptWindow => ({
    id,
    prompt: id,
    start: 0,
    duration: 1,
});

const retake = (id: string): CanonicalRetake => ({
    id,
    startSeconds: 0,
    lengthSeconds: 1,
    strength: 0.5,
});

const clip = (id: string): CanonicalClip => ({
    id,
    skipped: false,
    hue: 20,
    boundaryOut: "cut",
    boundaryOutOverlap: 8,
    duration: 4,
    audioSource: "Native",
    icLoras: [],
    saveAudioTrack: false,
    clipLengthFromAudio: false,
    clipLengthFromControlNet: false,
    reuseAudio: false,
    uploadedAudio: null,
    audioSegments: [],
    prompt: id,
    promptWindows: [],
    retake: null,
    sourceVideo: null,
    refs: [],
    stages: [],
});

const span = (id: string): CanonicalAudioTrackSpan => ({
    id,
    firstClipId: "clip-a",
    lastClipId: "clip-a",
    timelineStartSeconds: null,
    timelineLengthSeconds: null,
    sourceStartSeconds: 0,
    clipStartOffsetSeconds: null,
    clipLengthSeconds: null,
});

const track = (id: string): CanonicalAudioTrack => ({
    id,
    source: {
        kind: "External",
        reference: id,
        uploadedAudio: null,
    },
    spans: [],
});

const document = (): CanonicalVideoStagesConfig => {
    const clipA = clip("clip-a");
    clipA.stages = [stage("stage-a"), stage("stage-b"), stage("stage-c")];
    clipA.refs = [ref("ref-a"), ref("ref-b"), ref("ref-c")];
    clipA.audioSegments = [
        segment("segment-a"),
        segment("segment-b"),
        segment("segment-c"),
    ];
    clipA.promptWindows = [
        window("window-a"),
        window("window-b"),
        window("window-c"),
    ];
    clipA.retake = retake("retake-a");
    const trackA = track("track-a");
    trackA.spans = [span("span-a"), span("span-b"), span("span-c")];
    return {
        schemaVersion: 2,
        width: 1024,
        height: 576,
        fps: 24,
        dimsExplicit: true,
        fpsExplicit: true,
        clips: [clipA, clip("clip-b"), clip("clip-c")],
        audioTracks: [trackA, track("track-b"), track("track-c")],
    };
};

const applyDiff = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
): ReturnType<typeof diffDocuments> => {
    const command = diffDocuments(before, after);
    const result = reduceDocumentCommand(before, command);
    expect(result.applied).toBe(true);
    expect(result.document).toEqual(after);
    expect(before).toEqual(document());
    return command;
};

describe("diffDocuments", () => {
    it("produces an empty atomic batch for a no-op", () => {
        const before = document();
        const command = diffDocuments(before, structuredClone(before));
        const result = reduceDocumentCommand(before, command);

        expect(command).toEqual({ type: "batch", commands: [] });
        expect(result).toMatchObject({ applied: true, impacts: [] });
        expect(result.document).toEqual(before);
        expect(result.document).not.toBe(before);
    });

    it.each([
        {
            name: "root",
            type: "root.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.width = 768;
                after.schemaVersion = 3;
            },
        },
        {
            name: "clip with deep IC-LoRA values",
            type: "clip.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.clips[0].icLoras = [
                    {
                        lora: "guide",
                        preset: "custom",
                        source: "Upload",
                        stage: -1,
                        strength: 0.75,
                        attentionStrength: 0.5,
                        controlType: "depth",
                        video: null,
                        driveAudioRef: false,
                    },
                ];
            },
        },
        {
            name: "stage with owned arrays",
            type: "stage.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.clips[0].stages[0].loras = [
                    { name: "detail", weight: 0.6 },
                ];
                after.clips[0].stages[0].refStrengths = [0.9, 0.4];
            },
        },
        {
            name: "reference",
            type: "ref.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.clips[0].refs[0].frame = 9;
                after.clips[0].refs[0].uploadedImage = {
                    data: "data:image/png;base64,AA==",
                    fileName: "r.png",
                };
            },
        },
        {
            name: "audio segment",
            type: "audio-segment.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.clips[0].audioSegments[0].source = {
                    data: "data:audio/wav;base64,AA==",
                    fileName: "s.wav",
                };
                after.clips[0].audioSegments[0].lengthSeconds = 2;
            },
        },
        {
            name: "prompt window",
            type: "prompt-window.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.clips[0].promptWindows[0].prompt = "changed";
            },
        },
        {
            name: "retake",
            type: "retake.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                if (after.clips[0].retake) {
                    after.clips[0].retake.strength = 0.9;
                }
            },
        },
        {
            name: "audio track source",
            type: "audio-track.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.audioTracks[0].source = {
                    kind: "AceStepFun",
                    reference: "audio2",
                    uploadedAudio: null,
                };
            },
        },
        {
            name: "audio span",
            type: "audio-span.patch",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.audioTracks[0].spans[0].lastClipId = "clip-c";
                after.audioTracks[0].spans[0].timelineStartSeconds = 1;
                after.audioTracks[0].spans[0].timelineLengthSeconds = 3;
            },
        },
    ])("round-trips a $name value edit", ({ type, mutate }) => {
        const before = document();
        const after = structuredClone(before);
        mutate(after);

        const command = applyDiff(before, after);
        expect(command.commands.map((entry) => entry.type)).toEqual([type]);
        expect(
            command.commands.some(
                (entry) =>
                    (entry as { type: string }).type === "document.replace",
            ),
        ).toBe(false);
    });

    it.each([
        {
            name: "clips",
            removeType: "clip.remove",
            addType: "clip.add",
            moveType: "clip.move",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.clips = [
                    after.clips[2],
                    clip("clip-new"),
                    after.clips[1],
                ];
            },
        },
        {
            name: "stages",
            removeType: "stage.remove",
            addType: "stage.add",
            moveType: "stage.move",
            mutate: (after: CanonicalVideoStagesConfig) => {
                const items = after.clips[0].stages;
                after.clips[0].stages = [
                    items[2],
                    stage("stage-new"),
                    items[1],
                ];
            },
        },
        {
            name: "references",
            removeType: "ref.remove",
            addType: "ref.add",
            moveType: "ref.move",
            mutate: (after: CanonicalVideoStagesConfig) => {
                const items = after.clips[0].refs;
                after.clips[0].refs = [items[2], ref("ref-new"), items[1]];
            },
        },
        {
            name: "audio segments",
            removeType: "audio-segment.remove",
            addType: "audio-segment.add",
            moveType: "audio-segment.move",
            mutate: (after: CanonicalVideoStagesConfig) => {
                const items = after.clips[0].audioSegments;
                after.clips[0].audioSegments = [
                    items[2],
                    segment("segment-new"),
                    items[1],
                ];
            },
        },
        {
            name: "prompt windows",
            removeType: "prompt-window.remove",
            addType: "prompt-window.add",
            moveType: "prompt-window.move",
            mutate: (after: CanonicalVideoStagesConfig) => {
                const items = after.clips[0].promptWindows;
                after.clips[0].promptWindows = [
                    items[2],
                    window("window-new"),
                    items[1],
                ];
            },
        },
        {
            name: "audio tracks",
            removeType: "audio-track.remove",
            addType: "audio-track.add",
            moveType: "audio-track.move",
            mutate: (after: CanonicalVideoStagesConfig) => {
                after.audioTracks = [
                    after.audioTracks[2],
                    track("track-new"),
                    after.audioTracks[1],
                ];
            },
        },
        {
            name: "audio spans",
            removeType: "audio-span.remove",
            addType: "audio-span.add",
            moveType: "audio-span.move",
            mutate: (after: CanonicalVideoStagesConfig) => {
                const items = after.audioTracks[0].spans;
                after.audioTracks[0].spans = [
                    items[2],
                    span("span-new"),
                    items[1],
                ];
            },
        },
    ])("round-trips nested add/remove/reorder operations for $name", ({
        removeType,
        addType,
        moveType,
        mutate,
    }) => {
        const before = document();
        const after = structuredClone(before);
        mutate(after);

        const command = applyDiff(before, after);
        const types: string[] = command.commands.map((entry) => entry.type);
        expect(types).toEqual(
            expect.arrayContaining([removeType, addType, moveType]),
        );
        expect(types.indexOf(removeType)).toBeLessThan(types.indexOf(addType));
        expect(types.indexOf(addType)).toBeLessThan(types.indexOf(moveType));
    });

    it("replaces a retake by remove/add and preserves atomic target state", () => {
        const before = document();
        const after = structuredClone(before);
        after.clips[0].retake = retake("retake-new");

        const command = applyDiff(before, after);
        expect(command.commands.map((entry) => entry.type)).toEqual([
            "retake.remove",
            "retake.add",
        ]);
    });

    it("moves nested identities between owners using globally phased remove/add commands", () => {
        const before = document();
        const after = structuredClone(before);
        const movedStage = after.clips[0].stages.shift() as CanonicalStage;
        after.clips[1].stages.push(movedStage);
        const movedSpan =
            after.audioTracks[0].spans.shift() as CanonicalAudioTrackSpan;
        after.audioTracks[1].spans.push(movedSpan);

        const command = applyDiff(before, after);
        const types = command.commands.map((entry) => entry.type);
        expect(types).toEqual(
            expect.arrayContaining([
                "stage.remove",
                "audio-span.remove",
                "stage.add",
                "audio-span.add",
            ]),
        );
        expect(types.lastIndexOf("audio-span.remove")).toBeLessThan(
            types.indexOf("stage.add"),
        );
    });

    it.each([
        [
            "duplicate-id",
            (after: CanonicalVideoStagesConfig) => {
                after.clips[0].stages[0].id = "clip-a";
            },
        ],
        [
            "blank-id",
            (after: CanonicalVideoStagesConfig) => {
                after.audioTracks[0].spans[0].id = " ";
            },
        ],
        [
            "missing-id",
            (after: CanonicalVideoStagesConfig) => {
                (after.clips[0].refs[0] as { id?: string }).id = undefined;
            },
        ],
    ] as const)("rejects %s documents before producing commands", (_, mutate) => {
        const before = document();
        const after = structuredClone(before);
        mutate(after);

        try {
            diffDocuments(before, after);
            throw new Error("expected diff to reject invalid identity");
        } catch (error) {
            expect(error).toBeInstanceOf(DocumentDiffError);
            expect((error as DocumentDiffError).failure).toBe(
                _ === "duplicate-id" ? "duplicate-id" : "invalid-id",
            );
        }
    });

    it.each([
        ["a-b-c", ["clip-a", "clip-b", "clip-c"]],
        ["a-c-b", ["clip-a", "clip-c", "clip-b"]],
        ["b-a-c", ["clip-b", "clip-a", "clip-c"]],
        ["b-c-a", ["clip-b", "clip-c", "clip-a"]],
        ["c-a-b", ["clip-c", "clip-a", "clip-b"]],
        ["c-b-a", ["clip-c", "clip-b", "clip-a"]],
    ])("round-trips clip-order property %s", (_, ids) => {
        const before = document();
        const after = structuredClone(before);
        const byId = new Map(after.clips.map((item) => [item.id, item]));
        after.clips = ids.map((id) => byId.get(id) as CanonicalClip);
        applyDiff(before, after);
    });
});
