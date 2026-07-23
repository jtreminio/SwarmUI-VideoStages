import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "./__test_helpers__/architectureFixtures";
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
    modelProfileId: "ltx-2.3",
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
    volume: 1,
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
    architecture: "ltx2",
    modelProfileId: "ltx-2.3",
    skipped: false,
    hue: 0,
    boundaryOut: "cut",
    boundaryOutCarryAudio: false,
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
        schemaVersion: 3,
        width: 1024,
        height: 576,
        fps: 24,
        dimsExplicit: true,
        fpsExplicit: true,
        clips: [first, second],
        audioTracks: [firstTrack, track("track-b")],
    };
};

const catalogWithFake = (
    fake = fakeArchitectureCatalog(),
): ReturnType<typeof testArchitectureCatalog> => {
    const ltx = testArchitectureCatalog();
    return {
        source: "backend",
        architectures: [...ltx.architectures, ...fake.architectures],
        entries: [...ltx.entries, ...fake.entries],
    };
};

const catalogWithSecondLtxProfile = (): ReturnType<
    typeof testArchitectureCatalog
> => {
    const catalog = testArchitectureCatalog();
    catalog.architectures[0].profiles.push({
        id: "ltx-alt",
        label: "LTX Alternate Profile",
        capabilities: ["normal-lora"],
        rules: [],
    });
    catalog.entries.push({
        ...catalog.entries[0],
        value: "ltx-alt",
        label: "LTX Alternate",
        modelProfileId: "ltx-alt",
    });
    return catalog;
};

const apply = (
    source: CanonicalVideoStagesConfig,
    command: DocumentCommand,
): CanonicalVideoStagesConfig => {
    const result = reduceDocumentCommand(source, command, {
        architectureCatalog: catalogWithFake(),
    });
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
        const result = reduceDocumentCommand(
            source,
            {
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
            },
            { architectureCatalog: catalogWithFake() },
        );

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

    it("converts a clip architecture atomically and forces only cross-architecture joins to cuts", () => {
        const source = document();
        source.clips.push(clip("clip-c"));
        source.clips[0].boundaryOut = "crossfade";
        const targetClip = source.clips[1];
        targetClip.boundaryOut = "continue";
        targetClip.duration = 7;
        targetClip.prompt = "preserve me";
        targetClip.refs = [ref("ref-convert")];
        targetClip.promptWindows = [window("window-convert")];
        targetClip.audioSegments = [segment("segment-convert")];
        targetClip.retake = retake("retake-convert");
        targetClip.audioSource = "Upload";
        targetClip.uploadedAudio = {
            data: "data:audio/wav;base64,AAAA",
            fileName: "audio.wav",
        };
        targetClip.saveAudioTrack = true;
        targetClip.clipLengthFromAudio = true;
        targetClip.reuseAudio = true;
        targetClip.sourceVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 8,
            startSeconds: 1,
            lengthSeconds: 7,
        };
        targetClip.icLoras = [
            {
                lora: "hdr.safetensors",
                preset: "hdr",
                driveSource: "Upload",
                driveData: "visual",
                driveMediaKinds: ["image", "video"],
                stage: -1,
                strength: 1,
                attentionStrength: 1,
                controlType: "none",
                driveMedia: null,
            },
        ];
        targetClip.stages = [
            {
                ...stage("stage-convert-a"),
                refStrengths: [0.8],
                upscale: 2,
                loras: [{ name: "detail", weight: 1 }],
            },
            { ...stage("stage-convert-b"), skipped: true },
        ];
        const before = structuredClone(source);
        const fake = fakeArchitectureCatalog();
        const result = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-b",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video.safetensors",
                    capabilities: fake.architectures[0].capabilities,
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(result.applied).toBe(true);
        expect(result.impacts).toEqual([
            "value",
            "structure",
            "selection",
            "capabilities",
        ]);
        const converted = result.document.clips[1];
        expect(converted).toMatchObject({
            id: "clip-b",
            architecture: "test-video",
            modelProfileId: "test-profile",
            boundaryOut: "continue",
            duration: 7,
            prompt: "preserve me",
            refs: [],
            promptWindows: [],
            audioSegments: [],
            retake: null,
            sourceVideo: null,
            icLoras: [],
            audioSource: "Native",
            uploadedAudio: null,
            saveAudioTrack: false,
            clipLengthFromAudio: false,
            reuseAudio: false,
        });
        expect(result.document.clips[0].boundaryOut).toBe("cut");
        expect(
            converted.stages.map((item) => ({
                id: item.id,
                skipped: item.skipped,
                model: item.model,
                modelProfileId: item.modelProfileId,
                refStrengths: item.refStrengths,
                upscale: item.upscale,
                loras: item.loras,
            })),
        ).toEqual([
            {
                id: "stage-convert-a",
                skipped: false,
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
                refStrengths: [],
                upscale: 1,
                loras: [],
            },
            {
                id: "stage-convert-b",
                skipped: true,
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
                refStrengths: [],
                upscale: 1,
                loras: [],
            },
        ]);
        expect(source).toEqual(before);
    });

    it("preserves requested cross-architecture joins on ordinary clip edits", () => {
        const source = document();
        source.clips[0].boundaryOut = "continue";
        source.clips[1].skipped = true;
        const foreign = clip("clip-foreign");
        foreign.architecture = "test-video";
        foreign.modelProfileId = "test-profile";
        foreign.stages = [
            {
                ...stage("stage-foreign"),
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
            },
        ];

        const result = reduceDocumentCommand(
            source,
            { type: "clip.add", clip: foreign },
            { architectureCatalog: catalogWithFake() },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0].boundaryOut).toBe("continue");
    });

    it("removes incompatible later stages and repairs their IC-LoRA targets in one selection-affecting command", () => {
        const source = document();
        const targetClip = source.clips[1];
        targetClip.icLoras = [
            {
                lora: "guide.safetensors",
                preset: "custom",
                driveSource: "Incoming",
                driveData: "visual",
                driveMediaKinds: ["image", "video"],
                stage: 1,
                strength: 1,
                attentionStrength: 1,
                controlType: "none",
                driveMedia: null,
            },
        ];
        targetClip.stages = [
            stage("stage-convert-a"),
            stage("stage-convert-b"),
        ];
        const before = structuredClone(source);
        const fake = fakeArchitectureCatalog();
        const capabilities = fake.architectures[0].capabilities;
        capabilities.architecture = capabilities.architecture.filter(
            (feature) => feature !== "multi-stage",
        );
        capabilities.stage = [...capabilities.stage, "ic-lora"];

        const result = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-b",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video.safetensors",
                    capabilities,
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(result.applied).toBe(true);
        expect(result.impacts).toEqual([
            "value",
            "structure",
            "selection",
            "capabilities",
        ]);
        expect(result.document.clips[1].stages).toHaveLength(1);
        expect(result.document.clips[1].icLoras).toEqual([
            expect.objectContaining({
                stage: -1,
                driveSource: "Incoming",
                driveData: "visual",
            }),
        ]);
        expect(source).toEqual(before);
        expect(source.clips[1].stages.map(({ id }) => id)).toEqual([
            "stage-convert-a",
            "stage-convert-b",
        ]);
    });

    it("rejects forged generic identity patches at runtime", () => {
        const source = document();
        const clipPatch = reduceDocumentCommand(
            source,
            {
                type: "clip.patch",
                clipId: "clip-a",
                patch: { architecture: "test-video" },
            } as DocumentCommand,
            { architectureCatalog: catalogWithFake() },
        );
        const stagePatch = reduceDocumentCommand(
            source,
            {
                type: "stage.patch",
                clipId: "clip-a",
                stageId: "stage-a",
                patch: {
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                },
            } as DocumentCommand,
            { architectureCatalog: catalogWithFake() },
        );

        expect(clipPatch).toMatchObject({
            applied: false,
            failure: "architecture-invariant",
        });
        expect(stagePatch).toMatchObject({
            applied: false,
            failure: "architecture-invariant",
        });
        expect(clipPatch.document).toEqual(source);
        expect(stagePatch.document).toEqual(source);
    });

    it("validates skipped stage additions and retargets against the clip lock", () => {
        const source = document();
        const add = reduceDocumentCommand(
            source,
            {
                type: "stage.add",
                clipId: "clip-a",
                stage: {
                    ...stage("skipped-foreign"),
                    skipped: true,
                    model: "test-video.safetensors",
                    modelProfileId: "test-profile",
                },
            },
            { architectureCatalog: catalogWithFake() },
        );
        const retarget = reduceDocumentCommand(
            source,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-b",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video.safetensors",
                },
            },
            { architectureCatalog: catalogWithFake() },
        );

        expect(add.failure).toBe("architecture-invariant");
        expect(retarget.failure).toBe("architecture-invariant");
        expect(add.document).toEqual(source);
        expect(retarget.document).toEqual(source);
    });

    it("allows different catalog profiles across same-architecture active and skipped stages", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();
        const add = reduceDocumentCommand(
            source,
            {
                type: "stage.add",
                clipId: "clip-a",
                stage: {
                    ...stage("skipped-ltx-alt"),
                    skipped: true,
                    model: "ltx-alt",
                    modelProfileId: "ltx-alt",
                },
            },
            { architectureCatalog: catalog },
        );
        expect(add.applied).toBe(true);
        expect(add.document.clips[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });

        const retarget = reduceDocumentCommand(
            add.document,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-b",
                target: {
                    architectureId: "ltx2",
                    modelProfileId: "ltx-alt",
                    model: "ltx-alt",
                },
            },
            { architectureCatalog: catalog },
        );
        expect(retarget.applied).toBe(true);
        expect(retarget.document.clips[0].modelProfileId).toBe("ltx-2.3");
        expect(retarget.document.clips[0].stages[1]).toMatchObject({
            model: "ltx-alt",
            modelProfileId: "ltx-alt",
        });
    });

    it("keeps skipped Stage0 as the clip profile owner when Stage1 is active", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();
        source.clips[0].stages[0].skipped = true;
        source.clips[0].stages[1].model = "ltx-alt";
        source.clips[0].stages[1].modelProfileId = "ltx-alt";

        const result = reduceDocumentCommand(
            source,
            {
                type: "stage.patch",
                clipId: "clip-a",
                stageId: "stage-b",
                patch: { steps: 12 },
            },
            { architectureCatalog: catalog },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });
        expect(result.document.clips[0].stages[0]).toMatchObject({
            skipped: true,
            modelProfileId: "ltx-2.3",
        });
        expect(result.document.clips[0].stages[1]).toMatchObject({
            skipped: false,
            modelProfileId: "ltx-alt",
        });
    });

    it("updates the derived clip profile when Stage0 retargets within its architecture", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();

        const result = reduceDocumentCommand(
            source,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-a",
                target: {
                    architectureId: "ltx2",
                    modelProfileId: "ltx-alt",
                    model: "ltx-alt",
                },
            },
            { architectureCatalog: catalog },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-alt",
        });
        expect(result.document.clips[0].stages[0]).toMatchObject({
            model: "ltx-alt",
            modelProfileId: "ltx-alt",
        });
        expect(result.document.clips[0].stages[1].modelProfileId).toBe(
            "ltx-2.3",
        );
    });

    it("validates dormant source-only stages by architecture while retaining per-stage profiles", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();
        source.clips[0].sourceVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        source.clips[0].architecture = "none";
        source.clips[0].modelProfileId = "none";
        source.clips[0].stages[0].skipped = true;
        source.clips[0].stages[1] = {
            ...source.clips[0].stages[1],
            skipped: true,
            model: "ltx-alt",
            modelProfileId: "ltx-alt",
        };

        const result = reduceDocumentCommand(
            source,
            {
                type: "stage.patch",
                clipId: "clip-a",
                stageId: "stage-b",
                patch: { steps: 12 },
            },
            { architectureCatalog: catalog },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0]).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
        });
        expect(
            result.document.clips[0].stages.map(
                ({ modelProfileId }) => modelProfileId,
            ),
        ).toEqual(["ltx-2.3", "ltx-alt"]);
    });

    it("derives none and Stage0 identities across sourced skipped transitions", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();
        source.clips[0].sourceVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        source.clips[0].stages = [stage("stage-a")];

        const skipped = reduceDocumentCommand(
            source,
            {
                type: "stage.patch",
                clipId: "clip-a",
                stageId: "stage-a",
                patch: { skipped: true },
            },
            { architectureCatalog: catalog },
        );
        expect(skipped.document.clips[0]).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
        });

        const restored = reduceDocumentCommand(
            skipped.document,
            {
                type: "stage.patch",
                clipId: "clip-a",
                stageId: "stage-a",
                patch: { skipped: false },
            },
            { architectureCatalog: catalog },
        );
        expect(restored.document.clips[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });

        const removed = reduceDocumentCommand(
            restored.document,
            {
                type: "stage.remove",
                clipId: "clip-a",
                stageId: "stage-a",
            },
            { architectureCatalog: catalog },
        );
        expect(removed.document.clips[0]).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
            stages: [],
        });
    });

    it("updates the derived clip profile when a different-profile stage moves to Stage0", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();
        source.clips[0].stages[1].model = "ltx-alt";
        source.clips[0].stages[1].modelProfileId = "ltx-alt";

        const moved = reduceDocumentCommand(
            source,
            {
                type: "stage.move",
                clipId: "clip-a",
                stageId: "stage-b",
                beforeStageId: "stage-a",
            },
            { architectureCatalog: catalog },
        );

        expect(moved.applied).toBe(true);
        expect(moved.document.clips[0].modelProfileId).toBe("ltx-alt");
        expect(moved.document.clips[0].stages[0].id).toBe("stage-b");
    });

    it("reconciles effective identity when source video is attached or removed", () => {
        const source = document();
        const catalog = catalogWithFake();
        source.clips[0].stages.forEach((entry) => {
            entry.skipped = true;
        });
        const sourceVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };

        const attached = reduceDocumentCommand(
            source,
            {
                type: "clip.patch",
                clipId: "clip-a",
                patch: { sourceVideo },
            },
            { architectureCatalog: catalog },
        );
        expect(attached.document.clips[0]).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
        });

        const removed = reduceDocumentCommand(
            attached.document,
            {
                type: "clip.patch",
                clipId: "clip-a",
                patch: { sourceVideo: null },
            },
            { architectureCatalog: catalog },
        );
        expect(removed.document.clips[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("does not let forged retarget or conversion plans self-authorize", () => {
        const source = document();
        const catalog = catalogWithFake();
        const forgedRetarget = reduceDocumentCommand(
            source,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-a",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "ltx",
                },
            },
            { architectureCatalog: catalog },
        );
        const fake = fakeArchitectureCatalog();
        const forgedConversion = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-b",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "forged-profile",
                    model: "test-video.safetensors",
                    capabilities:
                        testArchitectureCatalog().architectures[0].capabilities,
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(forgedRetarget.failure).toBe("architecture-invariant");
        expect(forgedConversion.failure).toBe(
            "invalid-architecture-conversion",
        );
    });

    it("uses exact target profile LoRA and upscale-mode support during conversion", () => {
        const source = document();
        const fake = fakeArchitectureCatalog();
        fake.architectures[0].capabilities.stage = ["lora"];
        fake.architectures[0].capabilities.upscaleModes = ["pixel"];
        fake.architectures[0].profiles[0].capabilities = [];
        source.clips[1].stages = [
            {
                ...stage("model-upscale"),
                upscale: 2,
                upscaleMethod: "upscaler.safetensors",
                loras: [{ name: "detail", weight: 1 }],
            },
            {
                ...stage("pixel-upscale"),
                upscale: 2,
                upscaleMethod: "pixel-lanczos",
                loras: [{ name: "detail", weight: 1 }],
            },
        ];

        const result = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-b",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video.safetensors",
                    // This forged capability set deliberately over-claims support.
                    capabilities:
                        testArchitectureCatalog().architectures[0].capabilities,
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(result.applied).toBe(true);
        expect(
            result.document.clips[1].stages.map((entry) => ({
                upscale: entry.upscale,
                loras: entry.loras,
            })),
        ).toEqual([
            { upscale: 1, loras: [] },
            { upscale: 2, loras: [] },
        ]);
    });

    it("fails architecture-sensitive commands closed without a catalog", () => {
        const source = document();
        const result = reduceDocumentCommand(source, {
            type: "stage.add",
            clipId: "clip-a",
            stage: stage("stage-without-catalog"),
        });

        expect(result).toMatchObject({
            applied: false,
            failure: "architecture-invariant",
        });
    });
});
