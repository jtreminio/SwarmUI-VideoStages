import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import {
    minimalClip,
    minimalRef,
    minimalStage,
} from "./__test_helpers__/clipFixtures";
import { CONDITIONAL_RULE_CODES } from "./architectures/conditionalRules";
import {
    projectVideoExecutionPath,
    type VideoExecutionContext,
    type VideoHostEntryHint,
    type VideoTimelineShape,
} from "./executionPath";
import {
    type Clip,
    type IcLora,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
    type VideoStagesConfig,
} from "./types";

const config = (clips: Clip[]): VideoStagesConfig => ({
    width: 1280,
    height: 720,
    fps: 24,
    dimsExplicit: false,
    fpsExplicit: false,
    clips,
});

const sourceVideo = () => ({
    data: "data:video/mp4;base64,AA==",
    fileName: "source.mp4",
    fps: 24,
    durationSeconds: 5,
    startSeconds: 0,
    lengthSeconds: 5,
});

describe("projectVideoExecutionPath", () => {
    it.each<{
        name: string;
        clips: Clip[];
        context?: VideoExecutionContext;
        entry: VideoHostEntryHint;
        shape: VideoTimelineShape;
        labels: string[];
    }>([
        {
            name: "text-to-video, one generated stage",
            clips: [minimalClip()],
            entry: "text-to-video",
            shape: "single-clip-single-stage",
            labels: [
                "VideoStages",
                "LTX Video 2.3",
                "Text-to-video",
                "Single clip · single stage",
            ],
        },
        {
            name: "explicit host-image guidance",
            clips: [
                minimalClip({
                    refs: [minimalRef({ source: REF_SOURCE_BASE, frame: 3 })],
                }),
            ],
            context: { entryPoint: "host-image-guidance" },
            entry: "host-image-guidance",
            shape: "single-clip-single-stage",
            labels: ["Text → image → video", "1 frame reference"],
        },
        {
            name: "uploaded init image is inferred when no host hint is supplied",
            clips: [
                minimalClip({
                    refs: [minimalRef({ source: REF_SOURCE_UPLOAD, frame: 4 })],
                }),
            ],
            context: { entryPoint: "init-image-guidance" },
            entry: "init-image-guidance",
            shape: "single-clip-single-stage",
            labels: ["User-provided init image guidance", "1 frame reference"],
        },
        {
            name: "single clip with multiple active stages",
            clips: [minimalClip({ stages: [minimalStage(), minimalStage()] })],
            entry: "text-to-video",
            shape: "single-clip-multi-stage",
            labels: ["Single clip · multi-stage"],
        },
        {
            name: "multiple clips with one stage each",
            clips: [
                minimalClip(),
                minimalClip({ boundaryOut: "continue", boundaryOutOverlap: 8 }),
                minimalClip(),
            ],
            entry: "text-to-video",
            shape: "multi-clip-single-stage-each",
            labels: [
                "Multiple clips · single stage each",
                "2 clip boundaries: cut, continue",
            ],
        },
        {
            name: "multiple clips with a multi-stage clip",
            clips: [
                minimalClip({ stages: [minimalStage(), minimalStage()] }),
                minimalClip(),
            ],
            entry: "text-to-video",
            shape: "multi-clip-multi-stage",
            labels: ["Multiple clips · multi-stage"],
        },
        {
            name: "source video with active refinement stages",
            clips: [
                minimalClip({
                    sourceVideo: {
                        data: "data:video/mp4;base64,AA==",
                        fileName: "source.mp4",
                        fps: 24,
                        durationSeconds: 5,
                        startSeconds: 0,
                        lengthSeconds: 5,
                    },
                    stages: [minimalStage(), minimalStage()],
                }),
            ],
            entry: "source-video",
            shape: "single-clip-multi-stage",
            labels: ["1 source-video clip"],
        },
        {
            name: "source-video-only is represented when every stage is skipped",
            clips: [
                minimalClip({
                    sourceVideo: {
                        data: "data:video/mp4;base64,AA==",
                        fileName: "source.mp4",
                        fps: 24,
                        durationSeconds: 5,
                        startSeconds: 0,
                        lengthSeconds: 5,
                    },
                    stages: [minimalStage({ skipped: true })],
                }),
            ],
            entry: "source-video-only",
            shape: "single-clip-no-stage",
            labels: ["1 source-video-only clip"],
        },
    ])("summarizes $name", ({ clips, context, entry, shape, labels }) => {
        const summary = projectVideoExecutionPath(config(clips), context);

        expect(summary.hostEntry.kind).toBe(entry);
        expect(summary.shape.kind).toBe(shape);
        for (const label of labels) {
            expect(summary.labels).toContain(label);
        }
    });

    it("infers only clip-zero initial guidance and ignores later frame references", () => {
        const initial = projectVideoExecutionPath(
            config([
                minimalClip({
                    refs: [
                        minimalRef({
                            source: REF_SOURCE_UPLOAD,
                            frame: 1,
                            fromEnd: false,
                        }),
                    ],
                }),
            ]),
        );
        expect(initial.hostEntry.kind).toBe("init-image-guidance");

        const later = projectVideoExecutionPath(
            config([
                minimalClip(),
                minimalClip({
                    refs: [
                        minimalRef({
                            source: REF_SOURCE_UPLOAD,
                            frame: 1,
                            fromEnd: false,
                        }),
                    ],
                }),
            ]),
        );
        expect(later.hostEntry.kind).toBe("text-to-video");
    });

    it("represents the separate global Refine Video entry explicitly", () => {
        const summary = projectVideoExecutionPath(config([minimalClip()]), {
            entryPoint: "global-refine-video",
        });

        expect(summary.hostEntry).toEqual({
            kind: "global-refine-video",
            label: "Refine an existing video",
        });
        expect(summary.labels).toContain("Refine an existing video");
    });

    it("summarizes every optional LTX path feature without graph details", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    sourceVideo: sourceVideo(),
                    boundaryOut: "continue",
                    boundaryOutOverlap: 16,
                    stages: [
                        minimalStage({
                            upscale: 2,
                            loras: [{ name: "style.safetensors", weight: 0.8 }],
                        }),
                    ],
                    icLoras: [
                        {
                            lora: "guide.safetensors",
                            preset: "custom",
                            source: "Upload",
                            stage: -1,
                            strength: 1,
                            attentionStrength: 1,
                            controlType: "none",
                            video: null,
                            driveAudioRef: false,
                        },
                    ],
                    prompt: "major beat",
                    promptWindows: [
                        { prompt: "relay beat", start: 1, duration: 2 },
                    ],
                    retake: { startSeconds: 1, lengthSeconds: 1, strength: 1 },
                    refs: [
                        minimalRef({ source: REF_SOURCE_BASE, frame: 7 }),
                        minimalRef({
                            source: REF_SOURCE_UPLOAD,
                            frame: 4,
                            fromEnd: true,
                        }),
                    ],
                    audioSource: "audio2",
                    clipLengthFromAudio: true,
                    audioSegments: [
                        {
                            source: {
                                data: "data:audio/mp3;base64,AA==",
                                fileName: "hit.mp3",
                            },
                            startSeconds: 1,
                            trimStartSeconds: 0,
                            lengthSeconds: 1,
                        },
                    ],
                }),
                minimalClip({
                    boundaryOut: "crossfade",
                    clipLengthFromControlNet: true,
                    audioSource: "ControlNet",
                }),
                minimalClip(),
            ]),
        );

        expect(summary.boundaries).toEqual([
            expect.objectContaining({ kind: "continue", overlapFrames: 16 }),
            expect.objectContaining({ kind: "crossfade", overlapFrames: 8 }),
        ]);
        expect(summary.features).toMatchObject({
            upscaledStageCount: 1,
            icLoraCount: 1,
            loraCount: 1,
            majorPromptClipNumbers: [1],
            relayPromptCount: 1,
            retakeClipNumbers: [1],
            references: [
                expect.objectContaining({ frame: 7, fromEnd: false }),
                expect.objectContaining({ frame: 4, fromEnd: true }),
            ],
            audio: {
                segmentCount: 1,
                lengthFromAudioClipNumbers: [1],
                lengthFromControlNetClipNumbers: [2],
            },
        });
        expect(summary.features.audio.clips[0].label).toBe(
            "Clip 1: AceStepFun track 2 audio",
        );
        expect(
            summary.features.references.map((reference) => reference.label),
        ).toEqual([
            "Clip 1: host image at frame 7",
            "Clip 1: uploaded image at 4 frames from end",
        ]);
        expect(summary.labels).toEqual(
            expect.arrayContaining([
                "Upscaling in 1 stage",
                "1 IC-LoRA",
                "1 LoRA",
                "Major prompts: 1 clip override",
                "1 relay prompt",
                "1 retake",
                "2 frame references",
                "Audio sets 1 clip length",
                "ControlNet sets 1 clip length",
                "1 audio segment",
            ]),
        );
    });

    it("keeps skipped clips visible without counting their paths or options", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    skipped: true,
                    stages: [minimalStage({ upscale: 4 })],
                }),
                minimalClip(),
            ]),
        );

        expect(summary.counts).toMatchObject({
            clips: 2,
            executableClips: 1,
            stages: 2,
            activeStages: 1,
        });
        expect(summary.clips[0]).toMatchObject({
            kind: "skipped",
            label: "Clip 1: skipped · LTX Video 2.3",
        });
        expect(summary.features.upscaledStageCount).toBe(0);
    });

    it("counts and describes planned audio tracks, spans, and stable clip coverage", () => {
        const clips = [
            minimalClip({ id: "clip-a" }),
            minimalClip({ id: "clip-b" }),
            minimalClip({ id: "clip-c" }),
        ];
        const summary = projectVideoExecutionPath({
            ...config(clips),
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
                            firstClipId: "clip-a",
                            lastClipId: "clip-c",
                            timelineStartSeconds: 0.5,
                            timelineLengthSeconds: 4,
                            sourceStartSeconds: 2,
                            clipStartOffsetSeconds: null,
                            clipLengthSeconds: null,
                        },
                    ],
                },
                {
                    id: "track-pending",
                    source: {
                        kind: "External",
                        reference: "",
                        uploadedAudio: null,
                    },
                    spans: [
                        {
                            id: "span-pending",
                            firstClipId: "missing-clip",
                            lastClipId: "missing-clip",
                            timelineStartSeconds: null,
                            timelineLengthSeconds: null,
                            sourceStartSeconds: 0,
                            clipStartOffsetSeconds: null,
                            clipLengthSeconds: null,
                        },
                    ],
                },
            ],
        });

        expect(summary.counts).toMatchObject({
            authoredAudioTracks: 2,
            authoredAudioSpans: 2,
        });
        expect(summary.features.audio.authoredTracks[0]).toMatchObject({
            trackId: "track-score",
            clipNumbers: [1, 2, 3],
            pendingSpanCount: 0,
            label: "Track 1: audio1 · 1 span · clips 1–3",
        });
        expect(summary.features.audio.authoredTracks[0].spans[0]).toMatchObject(
            {
                firstClipNumber: 1,
                lastClipNumber: 3,
                clipNumbers: [1, 2, 3],
                pending: false,
                label: "Span 1: clips 1–3 · timeline 0.5–4.5s · source +2s",
            },
        );
        expect(summary.features.audio.authoredTracks[1]).toMatchObject({
            clipNumbers: [],
            pendingSpanCount: 1,
        });
        expect(
            summary.features.audio.authoredTracks[1].spans[0].label,
        ).toContain("unresolved clip coverage");
        expect(summary.labels).toContain(
            "2 planned audio tracks · 2 spans · 1 pending span",
        );
    });

    it("projects open-ended and clip-relative planned audio consistently", () => {
        const clips = [
            minimalClip({ id: "clip-a" }),
            minimalClip({ id: "clip-b" }),
            minimalClip({ id: "clip-c" }),
        ];
        const summary = projectVideoExecutionPath({
            ...config(clips),
            audioTracks: [
                {
                    id: "track-upload",
                    source: {
                        kind: "Upload",
                        reference: "",
                        uploadedAudio: {
                            data: "data:audio/wav;base64,AA==",
                            fileName: "score.wav",
                        },
                    },
                    spans: [
                        {
                            id: "open-start",
                            firstClipId: null,
                            lastClipId: "clip-b",
                            timelineStartSeconds: null,
                            timelineLengthSeconds: null,
                            sourceStartSeconds: 0,
                            clipStartOffsetSeconds: null,
                            clipLengthSeconds: null,
                        },
                        {
                            id: "timeline-owned",
                            firstClipId: null,
                            lastClipId: null,
                            timelineStartSeconds: 2,
                            timelineLengthSeconds: 4,
                            sourceStartSeconds: 0,
                            clipStartOffsetSeconds: null,
                            clipLengthSeconds: null,
                        },
                        {
                            id: "same-clip",
                            firstClipId: "clip-c",
                            lastClipId: "clip-c",
                            timelineStartSeconds: null,
                            timelineLengthSeconds: null,
                            sourceStartSeconds: 0,
                            clipStartOffsetSeconds: 0.5,
                            clipLengthSeconds: 1.5,
                        },
                        {
                            id: "invalid-multi-clip-relative",
                            firstClipId: "clip-a",
                            lastClipId: "clip-b",
                            timelineStartSeconds: null,
                            timelineLengthSeconds: null,
                            sourceStartSeconds: 0,
                            clipStartOffsetSeconds: 0,
                            clipLengthSeconds: 1,
                        },
                    ],
                },
            ],
        });
        const track = summary.features.audio.authoredTracks[0];

        expect(track).toMatchObject({
            sourceUploadFileName: "score.wav",
            pendingSpanCount: 1,
        });
        expect(track.label).toContain("score.wav");
        expect(track.spans[0]).toMatchObject({
            clipNumbers: [1, 2],
            pending: false,
        });
        expect(track.spans[1]).toMatchObject({
            clipNumbers: [],
            pending: false,
        });
        expect(track.spans[1].label).toContain("timeline window");
        expect(track.spans[2]).toMatchObject({
            clipNumbers: [3],
            pending: false,
        });
        expect(track.spans[2].label).toContain("within clip 0.5–2s");
        expect(track.spans[3].pending).toBe(true);
    });

    it("does not count an IC-LoRA whose selected stage is skipped or absent", () => {
        const icLora: IcLora = {
            lora: "guide.safetensors",
            preset: "custom",
            source: "Upload",
            stage: 1,
            strength: 1,
            attentionStrength: 1,
            controlType: "none",
            video: null,
            driveAudioRef: false,
        };
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    stages: [minimalStage(), minimalStage({ skipped: true })],
                    icLoras: [icLora, { ...icLora, stage: 4 }],
                }),
            ]),
        );

        expect(summary.features.icLoraCount).toBe(0);
    });

    it.each<{
        name: string;
        clips: Clip[];
        kind: VideoTimelineShape;
        label: string;
    }>([
        {
            name: "multiple source-only clips",
            clips: [
                minimalClip({ sourceVideo: sourceVideo(), stages: [] }),
                minimalClip({ sourceVideo: sourceVideo(), stages: [] }),
            ],
            kind: "multi-clip-no-stage",
            label: "Multiple clips · no generation stages",
        },
        {
            name: "source-only and single-stage clips",
            clips: [
                minimalClip({ sourceVideo: sourceVideo(), stages: [] }),
                minimalClip(),
            ],
            kind: "multi-clip-mixed-stages",
            label: "Multiple clips · mixed source-only, single-stage",
        },
        {
            name: "source-only, single-stage, and multi-stage clips",
            clips: [
                minimalClip({ sourceVideo: sourceVideo(), stages: [] }),
                minimalClip(),
                minimalClip({ stages: [minimalStage(), minimalStage()] }),
            ],
            kind: "multi-clip-mixed-stages",
            label: "Multiple clips · mixed source-only, single-stage, multi-stage",
        },
        {
            name: "single-stage and multi-stage clips remain multi-stage",
            clips: [
                minimalClip(),
                minimalClip({ stages: [minimalStage(), minimalStage()] }),
            ],
            kind: "multi-clip-multi-stage",
            label: "Multiple clips · multi-stage",
        },
    ])("describes $name truthfully", ({ clips, kind, label }) => {
        expect(projectVideoExecutionPath(config(clips)).shape).toEqual({
            kind,
            label,
        });
    });

    it.each<{
        name: string;
        clips: Clip[];
        context?: VideoExecutionContext;
        count: number;
    }>([
        {
            name: "generated stage zero is normalized to 1x",
            clips: [minimalClip({ stages: [minimalStage({ upscale: 2 })] })],
            count: 0,
        },
        {
            name: "generated stage one keeps its authored upscale",
            clips: [
                minimalClip({
                    stages: [
                        minimalStage({ upscale: 2 }),
                        minimalStage({ upscale: 2 }),
                    ],
                }),
            ],
            count: 1,
        },
        {
            name: "sourced stage zero keeps its authored upscale",
            clips: [
                minimalClip({
                    sourceVideo: sourceVideo(),
                    stages: [minimalStage({ upscale: 2 })],
                }),
            ],
            count: 1,
        },
        {
            name: "global refine stage zero is a skipped passthrough",
            clips: [
                minimalClip({
                    stages: [
                        minimalStage({ upscale: 2 }),
                        minimalStage({ upscale: 2 }),
                    ],
                }),
            ],
            context: { entryPoint: "global-refine-video" },
            count: 1,
        },
        {
            name: "global refine honors an explicitly longer skipped prefix",
            clips: [
                minimalClip({
                    stages: [
                        minimalStage({ upscale: 2 }),
                        minimalStage({ upscale: 2 }),
                        minimalStage({ upscale: 2 }),
                    ],
                }),
            ],
            context: {
                entryPoint: "global-refine-video",
                refineSkipStages: 2,
            },
            count: 1,
        },
    ])("counts effective upscaling when $name", ({ clips, context, count }) => {
        expect(
            projectVideoExecutionPath(config(clips), context).features
                .upscaledStageCount,
        ).toBe(count);
    });

    it("projects only post-skip clip-zero stages and their features during global refine", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    stages: [
                        minimalStage({
                            upscale: 2,
                            loras: [{ name: "old.safetensors", weight: 1 }],
                        }),
                        minimalStage({
                            upscale: 2,
                            loras: [
                                { name: "also-old.safetensors", weight: 1 },
                            ],
                        }),
                        minimalStage({
                            upscale: 2,
                            loras: [{ name: "new.safetensors", weight: 1 }],
                        }),
                    ],
                    icLoras: [
                        {
                            lora: "old-guide.safetensors",
                            preset: "custom",
                            source: "Upload",
                            stage: 1,
                            strength: 1,
                            attentionStrength: 1,
                            controlType: "none",
                            video: null,
                            driveAudioRef: false,
                        },
                        {
                            lora: "new-guide.safetensors",
                            preset: "custom",
                            source: "Upload",
                            stage: 2,
                            strength: 1,
                            attentionStrength: 1,
                            controlType: "none",
                            video: null,
                            driveAudioRef: false,
                        },
                    ],
                }),
            ]),
            {
                entryPoint: "global-refine-video",
                refineSkipStages: 2,
            },
        );

        expect(summary.counts.activeStages).toBe(1);
        expect(summary.shape.kind).toBe("single-clip-single-stage");
        expect(summary.features).toMatchObject({
            upscaledStageCount: 1,
            loraCount: 1,
            icLoraCount: 1,
        });
    });

    it.each<{
        name: string;
        clip: Clip;
        context?: VideoExecutionContext;
        expected: number[];
    }>([
        {
            name: "ordinary generated retake is ignored",
            clip: minimalClip({
                retake: {
                    startSeconds: 0,
                    lengthSeconds: 1,
                    strength: 1,
                },
            }),
            expected: [],
        },
        {
            name: "sourced retake executes",
            clip: minimalClip({
                sourceVideo: sourceVideo(),
                retake: {
                    startSeconds: 0,
                    lengthSeconds: 1,
                    strength: 1,
                },
            }),
            expected: [1],
        },
        {
            name: "global refine retake executes after its skipped prefix",
            clip: minimalClip({
                stages: [minimalStage(), minimalStage()],
                retake: {
                    startSeconds: 0,
                    lengthSeconds: 1,
                    strength: 1,
                },
            }),
            context: { entryPoint: "global-refine-video" },
            expected: [1],
        },
        {
            name: "source-only retake has no stage to execute it",
            clip: minimalClip({
                sourceVideo: sourceVideo(),
                stages: [],
                retake: {
                    startSeconds: 0,
                    lengthSeconds: 1,
                    strength: 1,
                },
            }),
            expected: [],
        },
    ])("reports only executable retakes: $name", ({
        clip,
        context,
        expected,
    }) => {
        expect(
            projectVideoExecutionPath(config([clip]), context).features
                .retakeClipNumbers,
        ).toEqual(expected);
    });

    it("projects boundaries over compacted executable clips using the left executable boundary", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    boundaryOut: "continue",
                    boundaryOutOverlap: 16,
                }),
                minimalClip({
                    skipped: true,
                    boundaryOut: "crossfade",
                }),
                minimalClip({ sourceVideo: null, stages: [] }),
                minimalClip(),
            ]),
        );

        expect(summary.boundaries).toEqual([
            expect.objectContaining({
                leftClipNumber: 1,
                rightClipNumber: 4,
                kind: "continue",
                requested: "continue",
                effective: "continue",
                fallback: "none",
                overlapFrames: 16,
            }),
        ]);
    });

    it.each<{
        name: string;
        target: Clip;
        fallback: string;
    }>([
        {
            name: "sourced target",
            target: minimalClip({ sourceVideo: sourceVideo() }),
            fallback: "target-is-sourced-video",
        },
        {
            name: "target with an explicit first-frame reference",
            target: minimalClip({
                refs: [minimalRef({ frame: 1, fromEnd: false })],
            }),
            fallback: "target-has-first-frame-reference",
        },
    ])("shows requested and effective continue boundary for $name", ({
        target,
        fallback,
    }) => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    boundaryOut: "continue",
                    boundaryOutOverlap: 16,
                }),
                target,
            ]),
            { catalog: testArchitectureCatalog() },
        );

        expect(summary.boundaries[0]).toMatchObject({
            requested: "continue",
            effective: "cut",
            fallback,
            overlapFrames: 0,
        });
        expect(summary.labels).toContain("1 clip boundary: continue→cut");
    });

    it.each<{
        name: string;
        context: VideoExecutionContext;
        expected: VideoHostEntryHint;
    }>([
        {
            name: "text-to-video ignores a Base first-frame reference",
            context: { generatedEntry: "text-to-video" },
            expected: "text-to-video",
        },
        {
            name: "host-image workflow needs no explicit authored reference",
            context: { generatedEntry: "host-image-guidance" },
            expected: "host-image-guidance",
        },
    ])("respects generated root entry: $name", ({ context, expected }) => {
        const refs =
            context.generatedEntry === "text-to-video"
                ? [minimalRef({ source: REF_SOURCE_BASE, frame: 1 })]
                : [];
        expect(
            projectVideoExecutionPath(config([minimalClip({ refs })]), context)
                .hostEntry.kind,
        ).toBe(expected);
    });

    it("keeps uploaded frame-one guidance as init-image even for text-to-video", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    refs: [
                        minimalRef({
                            source: REF_SOURCE_UPLOAD,
                            frame: 1,
                        }),
                    ],
                }),
            ]),
            { generatedEntry: "text-to-video" },
        );

        expect(summary.hostEntry.kind).toBe("init-image-guidance");
    });

    it("shows only uploaded references that LTX can execute on a text-to-video root", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    refs: [
                        minimalRef({ source: REF_SOURCE_BASE, frame: 1 }),
                        minimalRef({ source: REF_SOURCE_UPLOAD, frame: 3 }),
                    ],
                }),
                minimalClip({
                    refs: [
                        minimalRef({ source: REF_SOURCE_REFINER, frame: 4 }),
                        minimalRef({ source: REF_SOURCE_UPLOAD, frame: 6 }),
                    ],
                }),
            ]),
            { generatedEntry: "text-to-video" },
        );

        expect(summary.features.references).toMatchObject([
            { clipNumber: 1, frame: 3, source: REF_SOURCE_UPLOAD },
            { clipNumber: 2, frame: 6, source: REF_SOURCE_UPLOAD },
        ]);
        expect(summary.labels).toContain("2 frame references");
    });

    it("distinguishes clip prompt overrides from inherited global prompts", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({ prompt: "" }),
                minimalClip({ prompt: "clip override" }),
                minimalClip({ prompt: "   " }),
            ]),
            { globalPrompt: "global prompt" },
        );

        expect(summary.features).toMatchObject({
            majorPromptClipNumbers: [1, 2, 3],
            majorPromptOverrideClipNumbers: [2],
            majorPromptInheritedClipNumbers: [1, 3],
        });
        expect(summary.labels).toContain(
            "Major prompts: 1 clip override, 2 inherit global",
        );
    });

    it("surfaces clip-local audio source, eligible reuse, save, length, and segments", () => {
        const summary = projectVideoExecutionPath(
            config([
                minimalClip({
                    audioSource: "audio2",
                    stages: [minimalStage(), minimalStage(), minimalStage()],
                    reuseAudio: true,
                    saveAudioTrack: true,
                    clipLengthFromAudio: true,
                    audioSegments: [
                        {
                            source: "audio1",
                            startSeconds: 0,
                            trimStartSeconds: 0,
                            lengthSeconds: 1,
                        },
                    ],
                }),
                minimalClip({
                    audioSource: "ControlNet",
                    stages: [minimalStage(), minimalStage()],
                    reuseAudio: true,
                    clipLengthFromControlNet: true,
                }),
            ]),
        );

        expect(summary.features.audio.clips).toEqual([
            expect.objectContaining({
                label: "Clip 1: AceStepFun track 2 audio",
                reusesStageAudio: true,
                savesTrack: true,
                lengthFromAudio: true,
                segmentCount: 1,
            }),
            expect.objectContaining({
                label: "Clip 2: ControlNet audio",
                reusesStageAudio: false,
                lengthFromControlNet: true,
            }),
        ]);
        expect(summary.labels).toEqual(
            expect.arrayContaining([
                "Audio sources: AceStepFun track 2, ControlNet",
                "1 clip reuses captured stage audio",
                "1 saved audio output",
                "Audio sets 1 clip length",
                "ControlNet sets 1 clip length",
                "1 audio segment",
            ]),
        );
    });

    it("uses the catalog-advertised stage minimum for audio-reuse projection", () => {
        const catalog = testArchitectureCatalog();
        const reuseRule = catalog.architectures[0].rules.find(
            (rule) =>
                rule.code === CONDITIONAL_RULE_CODES.audioReuseRequiresStages,
        );
        if (!reuseRule) throw new Error("missing audio-reuse rule");
        reuseRule.constraints = {
            ...(reuseRule.constraints ?? {}),
            minimumActiveStages: 4,
        };
        const clips = [
            minimalClip({
                reuseAudio: true,
                stages: [minimalStage(), minimalStage(), minimalStage()],
            }),
        ];

        expect(
            projectVideoExecutionPath(config(clips), { catalog }).features.audio
                .clips[0].reusesStageAudio,
        ).toBe(false);
        clips[0].stages.push(minimalStage());
        expect(
            projectVideoExecutionPath(config(clips), { catalog }).features.audio
                .clips[0].reusesStageAudio,
        ).toBe(true);
    });

    it("reports an empty timeline plainly", () => {
        const summary = projectVideoExecutionPath(config([]));

        expect(summary.shape).toEqual({
            kind: "no-executable-clips",
            label: "No executable clips",
        });
        expect(summary.labels).toEqual([
            "VideoStages",
            "No active architecture",
            "Text-to-video",
            "No executable clips",
        ]);
    });
});
