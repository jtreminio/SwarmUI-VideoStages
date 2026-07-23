import { describe, expect, it } from "@jest/globals";
import {
    type ChangeImpact,
    type DocumentCommand,
    reduceDocumentCommand,
} from "./documentCommands";
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
    refStrengths: [],
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
    hue: 0,
    boundaryOut: "cut",
    boundaryOutOverlap: 9,
    duration: 4,
    audioSource: "Native",
    icLoras: [],
    saveAudioTrack: true,
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
    firstClipId: null,
    lastClipId: null,
    timelineStartSeconds: 0,
    timelineLengthSeconds: 1,
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
    const first = clip("clip-a");
    first.stages = [stage("stage-a"), stage("stage-b")];
    first.refs = [ref("ref-a")];
    first.audioSegments = [segment("segment-a")];
    first.promptWindows = [window("window-a")];
    first.retake = retake("retake-a");
    const second = clip("clip-b");
    const firstTrack = track("track-a");
    firstTrack.spans = [span("span-a"), span("span-b")];
    return {
        schemaVersion: 2,
        width: 1024,
        height: 576,
        fps: 24,
        dimsExplicit: true,
        fpsExplicit: true,
        clips: [first, second],
        audioTracks: [firstTrack, track("track-b")],
    };
};

const apply = (
    source: CanonicalVideoStagesConfig,
    command: DocumentCommand,
): CanonicalVideoStagesConfig => {
    const result = reduceDocumentCommand(source, command);
    expect(result.applied).toBe(true);
    return result.document;
};

describe("reduceDocumentCommand", () => {
    it("patches root settings on a clone and reports typed impacts", () => {
        const source = document();
        const result = reduceDocumentCommand(source, {
            type: "root.patch",
            patch: { width: 768, fpsExplicit: false },
        });

        const impacts: readonly ChangeImpact[] = ["value", "capabilities"];
        expect(result.impacts).toEqual(impacts);
        expect(result.document.width).toBe(768);
        expect(result.document.fpsExplicit).toBe(false);
        expect(source.width).toBe(1024);
        expect(result.document).not.toBe(source);
        expect(result.document.clips[0]).not.toBe(source.clips[0]);
    });

    it("adds, moves, patches, and removes clips entirely by ID", () => {
        let state = document();
        state = apply(state, {
            type: "clip.add",
            clip: clip("clip-c"),
            beforeClipId: "clip-b",
        });
        expect(state.clips.map((item) => item.id)).toEqual([
            "clip-a",
            "clip-c",
            "clip-b",
        ]);

        state = apply(state, {
            type: "clip.move",
            clipId: "clip-a",
            beforeClipId: null,
        });
        expect(state.clips.map((item) => item.id)).toEqual([
            "clip-c",
            "clip-b",
            "clip-a",
        ]);

        state = apply(state, {
            type: "clip.patch",
            clipId: "clip-c",
            patch: { duration: 9 },
        });
        expect(state.clips.find((item) => item.id === "clip-c")?.duration).toBe(
            9,
        );

        state = apply(state, { type: "clip.remove", clipId: "clip-b" });
        expect(state.clips.map((item) => item.id)).toEqual([
            "clip-c",
            "clip-a",
        ]);
    });

    it.each([
        {
            name: "stage",
            targetId: "stage-c",
            add: {
                type: "stage.add",
                clipId: "clip-a",
                stage: stage("stage-c"),
                beforeStageId: "stage-b",
            },
            move: {
                type: "stage.move",
                clipId: "clip-a",
                stageId: "stage-c",
                beforeStageId: "stage-a",
            },
            patch: {
                type: "stage.patch",
                clipId: "clip-a",
                stageId: "stage-c",
                patch: { steps: 12 },
            },
            remove: {
                type: "stage.remove",
                clipId: "clip-a",
                stageId: "stage-c",
            },
            ids: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].stages.map((item) => item.id),
            value: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].stages.find((item) => item.id === "stage-c")
                    ?.steps,
            expectedValue: 12,
        },
        {
            name: "reference",
            targetId: "ref-b",
            add: {
                type: "ref.add",
                clipId: "clip-a",
                ref: ref("ref-b"),
                beforeRefId: "ref-a",
            },
            move: {
                type: "ref.move",
                clipId: "clip-a",
                refId: "ref-b",
                beforeRefId: null,
            },
            patch: {
                type: "ref.patch",
                clipId: "clip-a",
                refId: "ref-b",
                patch: { frame: 3 },
            },
            remove: {
                type: "ref.remove",
                clipId: "clip-a",
                refId: "ref-b",
            },
            ids: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].refs.map((item) => item.id),
            value: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].refs.find((item) => item.id === "ref-b")?.frame,
            expectedValue: 3,
        },
        {
            name: "audio segment",
            targetId: "segment-b",
            add: {
                type: "audio-segment.add",
                clipId: "clip-a",
                segment: segment("segment-b"),
                beforeSegmentId: "segment-a",
            },
            move: {
                type: "audio-segment.move",
                clipId: "clip-a",
                segmentId: "segment-b",
                beforeSegmentId: null,
            },
            patch: {
                type: "audio-segment.patch",
                clipId: "clip-a",
                segmentId: "segment-b",
                patch: { lengthSeconds: 2 },
            },
            remove: {
                type: "audio-segment.remove",
                clipId: "clip-a",
                segmentId: "segment-b",
            },
            ids: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].audioSegments.map((item) => item.id),
            value: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].audioSegments.find(
                    (item) => item.id === "segment-b",
                )?.lengthSeconds,
            expectedValue: 2,
        },
        {
            name: "prompt window",
            targetId: "window-b",
            add: {
                type: "prompt-window.add",
                clipId: "clip-a",
                window: window("window-b"),
                beforeWindowId: "window-a",
            },
            move: {
                type: "prompt-window.move",
                clipId: "clip-a",
                windowId: "window-b",
                beforeWindowId: null,
            },
            patch: {
                type: "prompt-window.patch",
                clipId: "clip-a",
                windowId: "window-b",
                patch: { prompt: "updated" },
            },
            remove: {
                type: "prompt-window.remove",
                clipId: "clip-a",
                windowId: "window-b",
            },
            ids: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].promptWindows.map((item) => item.id),
            value: (state: CanonicalVideoStagesConfig) =>
                state.clips[0].promptWindows.find(
                    (item) => item.id === "window-b",
                )?.prompt,
            expectedValue: "updated",
        },
    ] satisfies Array<{
        name: string;
        targetId: string;
        add: DocumentCommand;
        move: DocumentCommand;
        patch: DocumentCommand;
        remove: DocumentCommand;
        ids: (state: CanonicalVideoStagesConfig) => string[];
        value: (state: CanonicalVideoStagesConfig) => unknown;
        expectedValue: unknown;
    }>)("supports the full stable-ID lifecycle for $name", ({
        targetId,
        add,
        move,
        patch,
        remove,
        ids,
        value,
        expectedValue,
    }) => {
        let state = apply(document(), add);
        expect(ids(state)).toContain(targetId);
        state = apply(state, move);
        expect(ids(state)).toContain(targetId);
        state = apply(state, patch);
        expect(value(state)).toBe(expectedValue);
        state = apply(state, remove);
        expect(ids(state)).not.toContain(targetId);
    });

    it("adds, patches, and removes the clip's single retake by its stable ID", () => {
        let state = apply(document(), {
            type: "retake.remove",
            clipId: "clip-a",
            retakeId: "retake-a",
        });
        state = apply(state, {
            type: "retake.add",
            clipId: "clip-a",
            retake: retake("retake-b"),
        });
        state = apply(state, {
            type: "retake.patch",
            clipId: "clip-a",
            retakeId: "retake-b",
            patch: { strength: 0.9 },
        });
        expect(state.clips[0].retake).toEqual({
            ...retake("retake-b"),
            strength: 0.9,
        });
    });

    it("supports stable-ID lifecycles for planned audio tracks and spans", () => {
        let state = apply(document(), {
            type: "audio-track.add",
            track: track("track-c"),
            beforeTrackId: "track-b",
        });
        state = apply(state, {
            type: "audio-track.move",
            trackId: "track-c",
            beforeTrackId: "track-a",
        });
        state = apply(state, {
            type: "audio-track.patch",
            trackId: "track-c",
            patch: {
                source: {
                    kind: "Native",
                    reference: "native",
                    uploadedAudio: null,
                },
            },
        });
        expect(state.audioTracks[0].id).toBe("track-c");
        expect(state.audioTracks[0].source.kind).toBe("Native");

        state = apply(state, {
            type: "audio-span.add",
            trackId: "track-c",
            span: span("span-c"),
        });
        state = apply(state, {
            type: "audio-span.patch",
            trackId: "track-c",
            spanId: "span-c",
            patch: { sourceStartSeconds: 2 },
        });
        expect(state.audioTracks[0].spans[0].sourceStartSeconds).toBe(2);
        state = apply(state, {
            type: "audio-span.move",
            trackId: "track-a",
            spanId: "span-b",
            beforeSpanId: "span-a",
        });
        expect(
            state.audioTracks.find((item) => item.id === "track-a")?.spans,
        ).toHaveLength(2);
        state = apply(state, {
            type: "audio-span.remove",
            trackId: "track-c",
            spanId: "span-c",
        });
        state = apply(state, {
            type: "audio-track.remove",
            trackId: "track-c",
        });
        expect(state.audioTracks.some((item) => item.id === "track-c")).toBe(
            false,
        );
    });

    it("fails closed when any owner, target, or ID-relative anchor is missing", () => {
        const source = document();
        const commands: DocumentCommand[] = [
            { type: "clip.remove", clipId: "missing" },
            {
                type: "stage.patch",
                clipId: "clip-a",
                stageId: "missing",
                patch: { steps: 99 },
            },
            {
                type: "ref.add",
                clipId: "clip-a",
                ref: ref("ref-b"),
                beforeRefId: "missing",
            },
            {
                type: "audio-span.remove",
                trackId: "missing",
                spanId: "span-a",
            },
        ];

        for (const command of commands) {
            const result = reduceDocumentCommand(source, command);
            expect(result).toMatchObject({
                applied: false,
                impacts: [],
                failure: "missing-target",
            });
            expect(result.document).toEqual(source);
            expect(result.document).not.toBe(source);
        }
    });

    it("rejects blank and duplicate IDs, including nested clip and track IDs", () => {
        const source = document();
        const duplicateClip = clip("clip-c");
        duplicateClip.stages = [stage("stage-a")];
        const duplicate = reduceDocumentCommand(source, {
            type: "clip.add",
            clip: duplicateClip,
        });
        expect(duplicate).toMatchObject({
            applied: false,
            failure: "duplicate-id",
        });

        const blank = reduceDocumentCommand(source, {
            type: "audio-track.add",
            track: track(" "),
        });
        expect(blank).toMatchObject({
            applied: false,
            failure: "invalid-id",
        });
    });

    it("preserves IDs and command payload isolation through patch and reorder", () => {
        const source = document();
        const patch = { loras: [{ name: "detail", weight: 0.5 }] };
        let state = apply(source, {
            type: "stage.patch",
            clipId: "clip-a",
            stageId: "stage-a",
            patch,
        });
        patch.loras[0].weight = 9;
        state = apply(state, {
            type: "stage.move",
            clipId: "clip-a",
            stageId: "stage-a",
            beforeStageId: null,
        });

        expect(state.clips[0].stages.map((item) => item.id)).toEqual([
            "stage-b",
            "stage-a",
        ]);
        expect(state.clips[0].stages[1].loras[0].weight).toBe(0.5);
        expect(source.clips[0].stages[0].loras).toEqual([]);
    });

    it("applies a batch atomically and de-duplicates combined impacts", () => {
        const source = document();
        const result = reduceDocumentCommand(source, {
            type: "batch",
            commands: [
                { type: "root.patch", patch: { fps: 30 } },
                {
                    type: "stage.patch",
                    clipId: "clip-a",
                    stageId: "stage-a",
                    patch: { steps: 12 },
                },
                {
                    type: "stage.move",
                    clipId: "clip-a",
                    stageId: "stage-b",
                    beforeStageId: "stage-a",
                },
                {
                    type: "prompt-window.remove",
                    clipId: "clip-a",
                    windowId: "window-a",
                },
            ],
        });

        expect(result.applied).toBe(true);
        expect(result.impacts).toEqual([
            "value",
            "structure",
            "selection",
            "capabilities",
        ]);
        expect(result.document.fps).toBe(30);
        expect(result.document.clips[0].stages[0]).toMatchObject({
            id: "stage-b",
        });
        expect(
            result.document.clips[0].stages.find(
                (item) => item.id === "stage-a",
            )?.steps,
        ).toBe(12);
        expect(result.document.clips[0].promptWindows).toEqual([]);
        expect(source).toEqual(document());
    });

    it("fails a batch without exposing any partial output", () => {
        const source = document();
        const result = reduceDocumentCommand(source, {
            type: "batch",
            commands: [
                { type: "root.patch", patch: { width: 640 } },
                {
                    type: "stage.patch",
                    clipId: "clip-a",
                    stageId: "missing",
                    patch: { steps: 99 },
                },
                { type: "clip.remove", clipId: "clip-b" },
            ],
        });

        expect(result).toMatchObject({
            applied: false,
            impacts: [],
            failure: "missing-target",
        });
        expect(result.document).toEqual(source);
        expect(result.document).not.toBe(source);
        expect(result.document.width).toBe(1024);
        expect(result.document.clips.map((item) => item.id)).toEqual([
            "clip-a",
            "clip-b",
        ]);
    });
});
