import { describe, expect, it } from "@jest/globals";
import {
    fakeArchitectureCatalog,
    testArchitectureCatalog,
} from "./__test_helpers__/architectureFixtures";
import { reconcileClipArchitectureIdentity } from "./architectures/clipIdentity";
import { planArchitectureConversion } from "./architectures/conversion/plan";
import type {
    ArchitectureModelCatalog,
    ArchitectureRetargetPlan,
} from "./architectures/types";
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
    hue: 20,
    boundaryOut: "cut",
    boundaryOutCarryAudio: false,
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
        schemaVersion: 3,
        width: 1024,
        height: 576,
        fps: 24,
        dimsExplicit: true,
        fpsExplicit: true,
        clips: [clipA, clip("clip-b"), clip("clip-c")],
        audioTracks: [trackA, track("track-b"), track("track-c")],
    };
};

const crossArchitectureCatalog = (): {
    catalog: ArchitectureModelCatalog;
    target: ArchitectureRetargetPlan;
} => {
    const ltx = testArchitectureCatalog();
    const fake = fakeArchitectureCatalog();
    fake.architectures[0].profiles.push({
        id: "test-alt",
        label: "Test Alt",
        capabilities: [],
        rules: [],
    });
    fake.entries.push({
        ...fake.entries[0],
        value: "test-video-alt.safetensors",
        label: "Test Video Alt",
        modelProfileId: "test-alt",
    });
    const catalog: ArchitectureModelCatalog = {
        source: "backend",
        architectures: [...ltx.architectures, ...fake.architectures],
        entries: [...ltx.entries, ...fake.entries],
    };
    return {
        catalog,
        target: {
            architectureId: "test-video",
            modelProfileId: "test-profile",
            model: "test-video.safetensors",
            capabilities: structuredClone(fake.architectures[0].capabilities),
        },
    };
};

const applyDiff = (
    before: CanonicalVideoStagesConfig,
    after: CanonicalVideoStagesConfig,
): ReturnType<typeof diffDocuments> => {
    const command = diffDocuments(before, after);
    const result = reduceDocumentCommand(before, command, {
        architectureCatalog: testArchitectureCatalog(),
    });
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

    it("fails closed instead of diffing raw clip architecture identities", () => {
        const before = document();
        const after = structuredClone(before);
        after.clips[0].architecture = "test-video";
        after.clips[0].modelProfileId = "test-profile";

        expect(() => diffDocuments(before, after)).toThrow(
            new DocumentDiffError("architecture-invariant"),
        );
        expect(() =>
            diffDocuments(before, after, {
                architectureCatalog: testArchitectureCatalog(),
            }),
        ).toThrow(new DocumentDiffError("architecture-invariant"));
    });

    it("accepts only catalog-derived source/skipped identity transitions", () => {
        const before = document();
        const after = structuredClone(before);
        after.clips[0].sourceVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        after.clips[0].stages.forEach((entry) => {
            entry.skipped = true;
        });
        after.clips[0].architecture = "none";
        after.clips[0].modelProfileId = "none";
        const catalog = testArchitectureCatalog();

        const command = diffDocuments(before, after, {
            architectureCatalog: catalog,
        });
        const result = reduceDocumentCommand(before, command, {
            architectureCatalog: catalog,
        });

        expect(result.applied).toBe(true);
        expect(result.document).toEqual(after);
        expect(command.commands).toEqual(
            expect.arrayContaining([
                expect.objectContaining({ type: "clip.patch" }),
                expect.objectContaining({ type: "stage.patch" }),
            ]),
        );
    });

    it("emits an invariant-aware model retarget instead of a generic stage patch", () => {
        const before = document();
        const after = structuredClone(before);
        after.clips[0].stages[1].model = "ltx-alt";
        const catalog = testArchitectureCatalog();
        catalog.entries.push({
            ...catalog.entries[0],
            value: "ltx-alt",
            label: "LTX Alt",
        });

        const command = diffDocuments(before, after);
        expect(command.commands).toContainEqual({
            type: "stage.retarget-model",
            clipId: "clip-a",
            stageId: "stage-b",
            target: {
                architectureId: "ltx2",
                modelProfileId: "ltx-2.3",
                model: "ltx-alt",
            },
        });
        expect(
            command.commands.some(
                (entry) =>
                    entry.type === "stage.patch" &&
                    ("model" in entry.patch || "modelProfileId" in entry.patch),
            ),
        ).toBe(false);
        const result = reduceDocumentCommand(before, command, {
            architectureCatalog: catalog,
        });
        expect(result.applied).toBe(true);
        expect(result.document).toEqual(after);
    });

    it("emits one cleanup-aware conversion before same-architecture stage retargets", () => {
        const before = document();
        before.clips[0].stages[0].loras = [
            { name: "detail.safetensors", weight: 1 },
        ];
        before.clips[0].stages[0].upscale = 2;
        before.clips[0].refs = [ref("conversion-ref")];
        const { catalog, target } = crossArchitectureCatalog();
        const planned = planArchitectureConversion(
            before.clips[0],
            target,
            catalog,
        );
        if (!planned) throw new Error("expected valid conversion plan");
        const after = structuredClone(before);
        after.clips[0] = planned.clip as CanonicalClip;
        after.clips[0].stages[1].model = "test-video-alt.safetensors";
        after.clips[0].stages[1].modelProfileId = "test-alt";

        const command = diffDocuments(before, after, {
            architectureCatalog: catalog,
        });
        const conversionIndexes = command.commands.flatMap((entry, index) =>
            entry.type === "clip.convert-architecture" ? [index] : [],
        );
        const retargetIndexes = command.commands.flatMap((entry, index) =>
            entry.type === "stage.retarget-model" ? [index] : [],
        );
        expect(conversionIndexes).toHaveLength(1);
        expect(retargetIndexes).toHaveLength(1);
        expect(conversionIndexes[0]).toBeLessThan(retargetIndexes[0]);
        expect(command.commands[retargetIndexes[0]]).toMatchObject({
            type: "stage.retarget-model",
            target: {
                architectureId: "test-video",
                modelProfileId: "test-alt",
                model: "test-video-alt.safetensors",
            },
        });

        const result = reduceDocumentCommand(before, command, {
            architectureCatalog: catalog,
        });
        expect(result.applied).toBe(true);
        expect(result.document).toEqual(after);
    });

    it("rejects cross-architecture saves without a catalog or with incompatible restored data", () => {
        const before = document();
        const { catalog, target } = crossArchitectureCatalog();
        const planned = planArchitectureConversion(
            before.clips[0],
            target,
            catalog,
        );
        if (!planned) throw new Error("expected valid conversion plan");
        const after = structuredClone(before);
        after.clips[0] = planned.clip as CanonicalClip;

        expect(() => diffDocuments(before, after)).toThrow(
            new DocumentDiffError("architecture-invariant"),
        );

        after.clips[0].refs = [ref("unsupported-restored-ref")];
        expect(() =>
            diffDocuments(before, after, { architectureCatalog: catalog }),
        ).toThrow(new DocumentDiffError("architecture-invariant"));
    });

    it("detects architecture conversion in dormant source-only authored stages", () => {
        const before = document();
        before.clips[0].sourceVideo = {
            data: "data:video/mp4;base64,AAAA",
            fileName: "source.mp4",
            fps: 24,
            durationSeconds: 4,
            startSeconds: 0,
            lengthSeconds: 4,
        };
        before.clips[0].stages.forEach((entry) => {
            entry.skipped = true;
        });
        before.clips[0].architecture = "none";
        before.clips[0].modelProfileId = "none";
        const { catalog, target } = crossArchitectureCatalog();
        const fake = catalog.architectures.find(
            (entry) => entry.id === "test-video",
        );
        if (!fake) throw new Error("missing fake descriptor");
        fake.capabilities.clip = [...fake.capabilities.clip, "source-video"];
        const planned = planArchitectureConversion(
            before.clips[0],
            target,
            catalog,
        );
        if (!planned) throw new Error("expected valid conversion plan");
        const after = structuredClone(before);
        after.clips[0] = planned.clip as CanonicalClip;
        expect(reconcileClipArchitectureIdentity(after.clips[0], catalog)).toBe(
            true,
        );
        expect(after.clips[0]).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
        });

        const command = diffDocuments(before, after, {
            architectureCatalog: catalog,
        });
        expect(
            command.commands.filter(
                (entry) => entry.type === "clip.convert-architecture",
            ),
        ).toHaveLength(1);
        const result = reduceDocumentCommand(before, command, {
            architectureCatalog: catalog,
        });
        expect(result.applied).toBe(true);
        expect(result.document).toEqual(after);
    });

    it("rejects a cross-architecture conversion that does not persist the forced cut", () => {
        const before = document();
        before.clips[0].boundaryOut = "continue";
        before.clips[1].stages = [stage("neighbor-stage")];
        const { catalog, target } = crossArchitectureCatalog();
        const planned = planArchitectureConversion(
            before.clips[0],
            target,
            catalog,
        );
        if (!planned) throw new Error("expected valid conversion plan");
        const after = structuredClone(before);
        after.clips[0] = planned.clip as CanonicalClip;

        expect(() =>
            diffDocuments(before, after, { architectureCatalog: catalog }),
        ).toThrow(new DocumentDiffError("architecture-invariant"));
    });

    it("round-trips adjacent conversions by their final boundary adjacency", () => {
        const before = document();
        before.clips[0].boundaryOut = "continue";
        before.clips[1].boundaryOut = "continue";
        before.clips[1].stages = [stage("second-conversion-stage")];
        const { catalog, target } = crossArchitectureCatalog();
        const after = structuredClone(before);
        for (const index of [0, 1]) {
            const planned = planArchitectureConversion(
                before.clips[index],
                target,
                catalog,
            );
            if (!planned) throw new Error("expected valid conversion plan");
            after.clips[index] = planned.clip as CanonicalClip;
        }

        const command = diffDocuments(before, after, {
            architectureCatalog: catalog,
        });
        expect(
            command.commands.filter(
                (entry) => entry.type === "clip.convert-architecture",
            ),
        ).toHaveLength(2);
        const result = reduceDocumentCommand(before, command, {
            architectureCatalog: catalog,
        });
        expect(result.applied).toBe(true);
        expect(result.document).toEqual(after);
        expect(result.document.clips[0].boundaryOut).toBe("continue");
    });

    it("rejects empty-clip identity changes that have no model target", () => {
        const before = document();
        before.clips[1].stages = [];
        const after = structuredClone(before);
        after.clips[1].architecture = "test-video";
        after.clips[1].modelProfileId = "test-profile";
        const { catalog } = crossArchitectureCatalog();

        expect(() =>
            diffDocuments(before, after, { architectureCatalog: catalog }),
        ).toThrow(new DocumentDiffError("architecture-invariant"));
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
