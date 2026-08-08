import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "./__test_helpers__/architectureFixtures";
import {
    canonicalAudioSpan,
    canonicalAudioTrack,
    canonicalClip,
    canonicalDocument,
    canonicalPromptWindow,
    canonicalRef,
    canonicalRetake,
    canonicalStage,
} from "./__test_helpers__/clipFixtures";
import {
    type DocumentCommand,
    reduceDocumentCommand,
} from "./documentCommands";
import type { CanonicalAuthoringDocument, CanonicalIcLora } from "./types";

const document = (): CanonicalAuthoringDocument =>
    canonicalDocument({
        clips: [
            canonicalClip("clip-a", {
                stages: [canonicalStage("stage-a"), canonicalStage("stage-b")],
                frameRefs: [canonicalRef("ref-a")],
                promptWindows: [canonicalPromptWindow("window-a")],
                retake: canonicalRetake("retake-a"),
            }),
            canonicalClip("clip-b"),
        ],
        audioTracks: [
            canonicalAudioTrack("track-a", {
                spans: [
                    canonicalAudioSpan("span-a"),
                    canonicalAudioSpan("span-b"),
                ],
            }),
            canonicalAudioTrack("track-b"),
        ],
    });

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
    catalog.entries.push({
        ...catalog.entries[0],
        value: "ltx-alt",
        label: "LTX Alternate",
        modelProfileId: "ltx-alt",
    });
    return catalog;
};

const imageDrivenIncomingIcLora = (): CanonicalIcLora => ({
    id: "ic-guide",
    lora: "guide.safetensors",
    preset: "custom",
    driveSource: "Incoming",
    driveData: "visual",
    driveMediaKinds: ["image"],
    stage: 0,
    strength: 1,
    attentionStrength: 1,
    controlType: "none",
    driveMedia: null,
});

const apply = (
    source: CanonicalAuthoringDocument,
    command: DocumentCommand,
): CanonicalAuthoringDocument => {
    const result = reduceDocumentCommand(source, command, {
        architectureCatalog: catalogWithFake(),
    });
    expect(result.applied).toBe(true);
    return result.document;
};

describe("reduceDocumentCommand", () => {
    it("patches root settings on a clone", () => {
        const source = document();
        const result = reduceDocumentCommand(source, {
            type: "root.patch",
            patch: { width: 768, dimsExplicit: false },
        });

        expect(result.document.width).toBe(768);
        expect(result.document.dimsExplicit).toBe(false);
        expect(source.width).toBe(1024);
        expect(result.document).not.toBe(source);
        expect(result.document.clips[0]).not.toBe(source.clips[0]);
    });

    it("adds, moves, patches, and removes clips entirely by ID", () => {
        let state = document();
        state = apply(state, {
            type: "clip.add",
            clip: canonicalClip("clip-c"),
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

    it("toggles clip skip suffixes atomically without mutating the source", () => {
        const source = document();
        source.clips.push(canonicalClip("clip-c"));
        const before = structuredClone(source);
        const skipped = reduceDocumentCommand(
            source,
            { type: "clip.toggle-skip", clipId: "clip-b" },
            { architectureCatalog: catalogWithFake() },
        );
        const restored = reduceDocumentCommand(
            skipped.document,
            { type: "clip.toggle-skip", clipId: "clip-c" },
            { architectureCatalog: catalogWithFake() },
        );

        expect(skipped.document.clips.map((item) => item.skipped)).toEqual([
            false,
            true,
            true,
        ]);
        expect(restored.document.clips.map((item) => item.skipped)).toEqual([
            false,
            false,
            false,
        ]);
        expect(source).toEqual(before);
    });

    it("toggles stage skip suffixes and reconciles init-video clip identity", () => {
        const source = document();
        source.clips[0].initVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        source.clips[0].stages.push(canonicalStage("stage-c"));
        const skipped = reduceDocumentCommand(
            source,
            {
                type: "stage.toggle-skip",
                clipId: "clip-a",
                stageId: "stage-b",
            },
            { architectureCatalog: catalogWithFake() },
        );
        expect(
            skipped.document.clips[0].stages.map((item) => item.skipped),
        ).toEqual([false, true, true]);

        skipped.document.clips[0].stages[0].skipped = true;
        skipped.document.clips[0].architectureHint = "none";
        skipped.document.clips[0].modelProfileId = "none";
        const restored = reduceDocumentCommand(
            skipped.document,
            {
                type: "stage.toggle-skip",
                clipId: "clip-a",
                stageId: "stage-a",
            },
            { architectureCatalog: catalogWithFake() },
        );

        expect(
            restored.document.clips[0].stages.map((item) => item.skipped),
        ).toEqual([false, false, false]);
        expect(restored.document.clips[0]).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("rejects active first-item and missing-target skip toggles unchanged", () => {
        const source = document();
        const firstClip = reduceDocumentCommand(
            source,
            { type: "clip.toggle-skip", clipId: "clip-a" },
            { architectureCatalog: catalogWithFake() },
        );
        const firstStage = reduceDocumentCommand(
            source,
            {
                type: "stage.toggle-skip",
                clipId: "clip-a",
                stageId: "stage-a",
            },
            { architectureCatalog: catalogWithFake() },
        );
        const missing = reduceDocumentCommand(
            source,
            { type: "clip.toggle-skip", clipId: "missing" },
            { architectureCatalog: catalogWithFake() },
        );

        expect(firstClip).toMatchObject({
            applied: false,
            failure: "invalid-operation",
            document: source,
        });
        expect(firstStage).toMatchObject({
            applied: false,
            failure: "invalid-operation",
            document: source,
        });
        expect(missing).toMatchObject({
            applied: false,
            failure: "missing-target",
            document: source,
        });
        expect(firstClip.document).not.toBe(source);
    });

    it("allows a legacy skipped first clip to be restored once", () => {
        const source = document();
        source.clips.forEach((item) => {
            item.skipped = true;
        });

        const restored = reduceDocumentCommand(
            source,
            { type: "clip.toggle-skip", clipId: "clip-a" },
            { architectureCatalog: catalogWithFake() },
        );
        const rejected = reduceDocumentCommand(
            restored.document,
            { type: "clip.toggle-skip", clipId: "clip-a" },
            { architectureCatalog: catalogWithFake() },
        );

        expect(restored.applied).toBe(true);
        expect(restored.document.clips.map((item) => item.skipped)).toEqual([
            false,
            false,
        ]);
        expect(rejected).toMatchObject({
            applied: false,
            failure: "invalid-operation",
        });
    });

    it("repairs graph-relative Incoming IC-LoRA drives in the same skip command", () => {
        const source = document();
        source.clips[1].stages = [canonicalStage("stage-b0")];
        source.clips[1].icLoras = [
            {
                id: "ic-guide",
                lora: "guide.safetensors",
                preset: "custom",
                driveSource: "Incoming",
                driveData: "visual",
                driveMediaKinds: ["video"],
                stage: 0,
                strength: 1,
                attentionStrength: 1,
                controlType: "none",
                driveMedia: null,
            },
        ];

        const result = reduceDocumentCommand(
            source,
            { type: "clip.toggle-skip", clipId: "clip-b" },
            {
                architectureCatalog: catalogWithFake(),
                generatedEntryMode: "text-to-video",
            },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[1].skipped).toBe(true);
        expect(result.document.clips[1].icLoras[0].driveSource).toBe("Upload");
    });

    it("takes a root the host has not named as text-to-video", () => {
        const source = document();
        source.clips[0].icLoras = [imageDrivenIncomingIcLora()];

        const result = reduceDocumentCommand(
            source,
            { type: "clip.toggle-skip", clipId: "clip-b" },
            { architectureCatalog: catalogWithFake() },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0].icLoras[0].driveSource).toBe("Upload");
    });

    it("takes an unnamed root as text-to-video when a stage skip repairs drives", () => {
        const source = document();
        source.clips[0].icLoras = [imageDrivenIncomingIcLora()];

        const result = reduceDocumentCommand(
            source,
            { type: "stage.toggle-skip", clipId: "clip-a", stageId: "stage-b" },
            { architectureCatalog: catalogWithFake() },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0].stages[1].skipped).toBe(true);
        expect(result.document.clips[0].icLoras[0].driveSource).toBe("Upload");
    });

    it("takes an unnamed root as text-to-video when a conversion repairs drives", () => {
        const source = document();
        const targetClip = source.clips[0];
        targetClip.architectureHint = "test-video";
        targetClip.modelProfileId = "test-profile";
        targetClip.stages.forEach((entry) => {
            entry.model = "test-video.safetensors";
            entry.modelProfileId = "test-profile";
        });
        targetClip.icLoras = [imageDrivenIncomingIcLora()];

        const result = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-a",
                target: {
                    architectureId: "ltx2",
                    modelProfileId: "ltx-2.3",
                    model: "ltx",
                },
            },
            { architectureCatalog: catalogWithFake() },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0].icLoras[0].driveSource).toBe("Upload");
    });

    it.each([
        {
            name: "stage",
            targetId: "stage-c",
            add: {
                type: "stage.add",
                clipId: "clip-a",
                stage: canonicalStage("stage-c"),
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
            ids: (state: CanonicalAuthoringDocument) =>
                state.clips[0].stages.map((item) => item.id),
            value: (state: CanonicalAuthoringDocument) =>
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
                ref: canonicalRef("ref-b"),
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
            ids: (state: CanonicalAuthoringDocument) =>
                state.clips[0].frameRefs.map((item) => item.id),
            value: (state: CanonicalAuthoringDocument) =>
                state.clips[0].frameRefs.find((item) => item.id === "ref-b")
                    ?.frame,
            expectedValue: 3,
        },
        {
            name: "prompt window",
            targetId: "window-b",
            add: {
                type: "prompt-window.add",
                clipId: "clip-a",
                window: canonicalPromptWindow("window-b"),
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
            ids: (state: CanonicalAuthoringDocument) =>
                state.clips[0].promptWindows.map((item) => item.id),
            value: (state: CanonicalAuthoringDocument) =>
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
        ids: (state: CanonicalAuthoringDocument) => string[];
        value: (state: CanonicalAuthoringDocument) => unknown;
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
            retake: canonicalRetake("retake-b"),
        });
        state = apply(state, {
            type: "retake.patch",
            clipId: "clip-a",
            retakeId: "retake-b",
            patch: { strength: 0.9 },
        });
        expect(state.clips[0].retake).toEqual({
            ...canonicalRetake("retake-b"),
            strength: 0.9,
        });
    });

    it("supports stable-ID lifecycles for planned audio tracks and spans", () => {
        let state = apply(document(), {
            type: "audio-track.add",
            track: canonicalAudioTrack("track-c"),
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
            span: canonicalAudioSpan("span-c"),
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
                ref: canonicalRef("ref-b"),
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
                failure: "missing-target",
            });
            expect(result.document).toEqual(source);
            expect(result.document).not.toBe(source);
        }
    });

    it("rejects blank and duplicate IDs, including nested clip and track IDs", () => {
        const source = document();
        const duplicateClip = canonicalClip("clip-c");
        duplicateClip.stages = [canonicalStage("stage-a")];
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
            track: canonicalAudioTrack(" "),
        });
        expect(blank).toMatchObject({
            applied: false,
            failure: "invalid-id",
        });
    });

    it("preserves IDs and command payload isolation through patch and reorder", () => {
        const source = document();
        const patch = { loraWeights: [0.5] };
        let state = apply(source, {
            type: "stage.patch",
            clipId: "clip-a",
            stageId: "stage-a",
            patch,
        });
        patch.loraWeights[0] = 9;
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
        expect(state.clips[0].stages[1].loraWeights[0]).toBe(0.5);
        expect(source.clips[0].stages[0].loraWeights).toEqual([]);
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
        source.clips.push(canonicalClip("clip-c"));
        source.clips[0].boundaryOut = "crossfade";
        const targetClip = source.clips[1];
        targetClip.boundaryOut = "continue";
        targetClip.duration = 7;
        targetClip.prompt = "preserve me";
        targetClip.frameRefs = [canonicalRef("ref-convert")];
        targetClip.promptWindows = [canonicalPromptWindow("window-convert")];
        targetClip.retake = canonicalRetake("retake-convert");
        targetClip.audioSource = "Upload";
        targetClip.uploadedAudio = {
            data: "data:audio/wav;base64,AAAA",
            fileName: "audio.wav",
        };
        targetClip.saveAudioTrack = true;
        targetClip.clipLengthFromAudio = true;
        targetClip.reuseAudio = true;
        targetClip.initVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 8,
            startSeconds: 1,
            lengthSeconds: 7,
        };
        targetClip.icLoras = [
            {
                id: "ic-pose",
                lora: "pose.safetensors",
                preset: "pose",
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
        targetClip.loras = [{ name: "detail" }];
        targetClip.stages = [
            {
                ...canonicalStage("stage-convert-a"),
                frameRefStrengths: [0.8],
                upscale: 2,
                loraWeights: [1],
            },
            { ...canonicalStage("stage-convert-b"), skipped: true },
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
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(result.applied).toBe(true);
        const converted = result.document.clips[1];
        expect(converted).toMatchObject({
            id: "clip-b",
            architectureHint: "test-video",
            modelProfileId: "test-profile",
            boundaryOut: "continue",
            duration: 7,
            prompt: "preserve me",
            frameRefs: targetClip.frameRefs,
            promptWindows: targetClip.promptWindows,
            retake: targetClip.retake,
            initVideo: targetClip.initVideo,
            icLoras: targetClip.icLoras,
            audioSource: "Upload",
            uploadedAudio: targetClip.uploadedAudio,
            saveAudioTrack: true,
            clipLengthFromAudio: true,
            reuseAudio: true,
        });
        expect(result.document.clips[0].boundaryOut).toBe("cut");
        expect(
            converted.stages.map((item) => ({
                id: item.id,
                skipped: item.skipped,
                model: item.model,
                modelProfileId: item.modelProfileId,
                frameRefStrengths: item.frameRefStrengths,
                upscale: item.upscale,
                loraWeights: item.loraWeights,
            })),
        ).toEqual([
            {
                id: "stage-convert-a",
                skipped: false,
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
                frameRefStrengths: [0.8],
                upscale: 2,
                loraWeights: [1],
            },
            {
                id: "stage-convert-b",
                skipped: true,
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
                frameRefStrengths: [],
                upscale: 1,
                loraWeights: [],
            },
        ]);
        expect(source).toEqual(before);
    });

    it("uses resolved models rather than stale hints when conversion repairs boundaries", () => {
        const source = document();
        source.clips.push(canonicalClip("clip-c"));
        source.clips[1].stages = [canonicalStage("stage-b-root")];
        source.clips[2].stages = [canonicalStage("stage-c-root")];
        source.clips[0].boundaryOut = "continue";
        source.clips[1].boundaryOut = "continue";
        // Both first joins are authored LTX by model, despite this stale hint.
        source.clips[1].architectureHint = "test-video";
        source.clips[1].modelProfileId = "test-profile";
        const fake = fakeArchitectureCatalog();

        const result = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-c",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video.safetensors",
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0].boundaryOut).toBe("continue");
        expect(result.document.clips[1].boundaryOut).toBe("cut");
    });

    it("preserves requested cross-architecture joins on ordinary clip edits", () => {
        const source = document();
        source.clips[0].boundaryOut = "continue";
        source.clips[1].skipped = true;
        const foreign = canonicalClip("clip-foreign");
        foreign.architectureHint = "test-video";
        foreign.modelProfileId = "test-profile";
        foreign.stages = [
            {
                ...canonicalStage("stage-foreign"),
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

    it("rejects forged generic identity patches at runtime", () => {
        const source = document();
        const clipPatch = reduceDocumentCommand(
            source,
            {
                type: "clip.patch",
                clipId: "clip-a",
                patch: { architectureHint: "test-video" },
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
                    ...canonicalStage("skipped-foreign"),
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

    it("rejects a cross-architecture stage retarget even when the clip has only one stage", () => {
        const source = document();
        source.clips[0].stages = [source.clips[0].stages[0]];

        const result = reduceDocumentCommand(
            source,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-a",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video.safetensors",
                },
            },
            { architectureCatalog: catalogWithFake() },
        );

        expect(result).toMatchObject({
            applied: false,
            failure: "architecture-invariant",
        });
        expect(result.document).toEqual(source);
    });

    it("uses resolved Stage 0 ownership instead of a stale cached architecture during retarget", () => {
        const source = document();
        const catalog = catalogWithFake();
        const current = catalog.entries.find(
            (entry) => entry.value === "test-video.safetensors",
        );
        if (!current) throw new Error("missing test video model");
        catalog.entries.push({
            ...current,
            value: "test-video-alt.safetensors",
            label: "Test Video Alternate",
        });
        source.clips[0].stages = [
            {
                ...source.clips[0].stages[0],
                model: "test-video.safetensors",
                modelProfileId: "test-profile",
            },
        ];

        const result = reduceDocumentCommand(
            source,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-a",
                target: {
                    architectureId: "test-video",
                    modelProfileId: "test-profile",
                    model: "test-video-alt.safetensors",
                },
            },
            { architectureCatalog: catalog },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0]).toMatchObject({
            architectureHint: "test-video",
            modelProfileId: "test-profile",
            stages: [
                {
                    model: "test-video-alt.safetensors",
                    modelProfileId: "test-profile",
                },
            ],
        });
    });

    it("uses authored Stage-0 model ownership for a dormant source-only retarget", () => {
        const source = document();
        const targetClip = source.clips[0];
        targetClip.initVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        targetClip.stages = [{ ...targetClip.stages[0], skipped: true }];
        targetClip.architectureHint = "none";
        targetClip.modelProfileId = "none";
        const catalog = testArchitectureCatalog();
        catalog.entries.push({
            ...catalog.entries[0],
            value: "ltx-dormant-alt",
            label: "LTX Dormant Alternate",
        });

        const result = reduceDocumentCommand(
            source,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-a",
                target: {
                    architectureId: "ltx2",
                    modelProfileId: "ltx-2.3",
                    model: "ltx-dormant-alt",
                },
            },
            { architectureCatalog: catalog },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0]).toMatchObject({
            architectureHint: "none",
            modelProfileId: "none",
            stages: [
                {
                    model: "ltx-dormant-alt",
                    modelProfileId: "ltx-2.3",
                    skipped: true,
                },
            ],
        });
    });

    it("requires whole-clip conversion to repair an unresolved Stage 0 model", () => {
        const source = document();
        source.clips[0].stages = [
            {
                ...source.clips[0].stages[0],
                model: "removed-model.safetensors",
                modelProfileId: "removed-profile",
            },
        ];
        const catalog = testArchitectureCatalog();

        const result = reduceDocumentCommand(
            source,
            {
                type: "stage.retarget-model",
                clipId: "clip-a",
                stageId: "stage-a",
                target: {
                    architectureId: "ltx2",
                    modelProfileId: "ltx-2.3",
                    model: "ltx",
                },
            },
            { architectureCatalog: catalog },
        );

        expect(result.applied).toBe(false);
        expect(result.failure).toBe("architecture-invariant");

        const conversion = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-a",
                target: {
                    architectureId: "ltx2",
                    modelProfileId: "ltx-2.3",
                    model: "ltx",
                },
            },
            { architectureCatalog: catalog },
        );

        expect(conversion.applied).toBe(true);
        expect(conversion.document.clips[0].stages[0]).toMatchObject({
            model: "ltx",
            modelProfileId: "ltx-2.3",
        });
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
                    ...canonicalStage("skipped-ltx-alt"),
                    skipped: true,
                    model: "ltx-alt",
                    modelProfileId: "ltx-alt",
                },
            },
            { architectureCatalog: catalog },
        );
        expect(add.applied).toBe(true);
        expect(add.document.clips[0]).toMatchObject({
            architectureHint: "ltx2",
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
            architectureHint: "ltx2",
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
            architectureHint: "ltx2",
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

    it("rejects a root retarget that would leave a skipped stage in another compatibility class", () => {
        const source = document();
        source.clips[0].stages[1].skipped = true;
        const catalog = catalogWithSecondLtxProfile();
        const alternate = catalog.entries.find(
            (entry) => entry.value === "ltx-alt",
        );
        if (!alternate) throw new Error("missing alternate LTX model");
        alternate.compatibilityClassId = "other-ltx-family";

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

        expect(result).toMatchObject({
            applied: false,
            failure: "architecture-invariant",
        });
        expect(result.document).toEqual(source);
    });

    it("validates dormant source-only stages by architecture while retaining per-stage profiles", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();
        source.clips[0].initVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        source.clips[0].architectureHint = "none";
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
            architectureHint: "none",
            modelProfileId: "none",
        });
        expect(
            result.document.clips[0].stages.map(
                ({ modelProfileId }) => modelProfileId,
            ),
        ).toEqual(["ltx-2.3", "ltx-alt"]);
    });

    it("derives none and Stage0 identities across init-video skipped transitions", () => {
        const source = document();
        const catalog = catalogWithSecondLtxProfile();
        source.clips[0].initVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        source.clips[0].stages = [canonicalStage("stage-a")];

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
            architectureHint: "none",
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
            architectureHint: "ltx2",
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
            architectureHint: "none",
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
        const initVideo = {
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
                patch: { initVideo },
            },
            { architectureCatalog: catalog },
        );
        expect(attached.document.clips[0]).toMatchObject({
            architectureHint: "none",
            modelProfileId: "none",
        });

        const removed = reduceDocumentCommand(
            attached.document,
            {
                type: "clip.patch",
                clipId: "clip-a",
                patch: { initVideo: null },
            },
            { architectureCatalog: catalog },
        );
        expect(removed.document.clips[0]).toMatchObject({
            architectureHint: "ltx2",
            modelProfileId: "ltx-2.3",
        });
    });

    it("rejects forged model ownership but treats profile ids as aliases", () => {
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
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(forgedRetarget.failure).toBe("architecture-invariant");
        expect(forgedConversion.applied).toBe(true);
        expect(forgedConversion.document.clips[1].modelProfileId).toBe(
            "test-profile",
        );
    });

    it("preserves unsupported LoRA and upscale settings as dormant data", () => {
        const source = document();
        const fake = fakeArchitectureCatalog();
        source.clips[1].loras = [{ name: "detail" }];
        source.clips[1].stages = [
            {
                ...canonicalStage("model-upscale"),
                upscale: 2,
                upscaleMethod: "upscaler.safetensors",
                loraWeights: [1],
            },
            {
                ...canonicalStage("pixel-upscale"),
                upscale: 2,
                upscaleMethod: "pixel-lanczos",
                loraWeights: [1],
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
                },
            },
            { architectureCatalog: catalogWithFake(fake) },
        );

        expect(result.applied).toBe(true);
        expect(
            result.document.clips[1].stages.map((entry) => ({
                upscale: entry.upscale,
                loraWeights: entry.loraWeights,
            })),
        ).toEqual([
            { upscale: 2, loraWeights: [1] },
            { upscale: 2, loraWeights: [1] },
        ]);
    });

    it("repairs dormant Incoming IC-LoRA media when conversion reactivates LTX behavior", () => {
        const source = document();
        const fake = fakeArchitectureCatalog();
        const catalog = catalogWithFake(fake);
        const targetClip = source.clips[0];
        targetClip.architectureHint = "test-video";
        targetClip.modelProfileId = "test-profile";
        targetClip.stages.forEach((entry) => {
            entry.model = "test-video.safetensors";
            entry.modelProfileId = "test-profile";
        });
        targetClip.icLoras = [
            {
                id: "ic-guide",
                lora: "guide.safetensors",
                preset: "custom",
                driveSource: "Incoming",
                driveData: "visual",
                driveMediaKinds: ["image", "video"],
                stage: 0,
                strength: 1,
                attentionStrength: 1,
                controlType: "none",
                driveMedia: null,
            },
        ];
        const result = reduceDocumentCommand(
            source,
            {
                type: "clip.convert-architecture",
                clipId: "clip-a",
                target: {
                    architectureId: "ltx2",
                    modelProfileId: "ltx-2.3",
                    model: "ltx",
                },
            },
            {
                architectureCatalog: catalog,
                generatedEntryMode: "text-to-video",
            },
        );

        expect(result.applied).toBe(true);
        expect(result.document.clips[0].icLoras[0].driveSource).toBe("Upload");
    });

    it("fails architecture-sensitive commands closed without a catalog", () => {
        const source = document();
        const result = reduceDocumentCommand(source, {
            type: "stage.add",
            clipId: "clip-a",
            stage: canonicalStage("stage-without-catalog"),
        });

        expect(result).toMatchObject({
            applied: false,
            failure: "architecture-invariant",
        });
    });
});
