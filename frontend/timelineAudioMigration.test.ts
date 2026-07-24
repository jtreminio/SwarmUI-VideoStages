import { describe, expect, it } from "@jest/globals";
import { minimalClip } from "./__test_helpers__/clipFixtures";
import { decodeStoredDocument } from "./persistence/documentCodec";
import { migrateClipAudioSegmentsToTimeline } from "./timelineAudioMigration";

describe("clip audio segment migration", () => {
    it("moves every legacy overlay onto absolute timeline time without losing source or volume", () => {
        const clips = [
            minimalClip({
                id: "clip-a",
                duration: 3,
                audioSegments: [
                    {
                        id: "old-a",
                        source: {
                            data: "data:audio/wav;base64,AA==",
                            fileName: "a.wav",
                        },
                        startSeconds: 1,
                        trimStartSeconds: 2,
                        lengthSeconds: 1.5,
                        volume: 0.5,
                    },
                ],
            }),
            minimalClip({
                id: "clip-b",
                duration: 4,
                audioSegments: [
                    {
                        id: "old-b",
                        source: "audio1",
                        startSeconds: 2,
                        trimStartSeconds: 0.5,
                        lengthSeconds: 2,
                        volume: 1.25,
                    },
                ],
            }),
        ];

        const tracks = migrateClipAudioSegmentsToTimeline(clips, []);

        expect(clips.map((clip) => clip.audioSegments)).toEqual([[], []]);
        expect(tracks).toHaveLength(2);
        expect(tracks[0]).toMatchObject({
            volume: 0.5,
            source: {
                kind: "Upload",
                reference: "a.wav",
                uploadedAudio: { fileName: "a.wav" },
            },
            spans: [
                {
                    id: "old-a",
                    timelineStartSeconds: 1,
                    timelineLengthSeconds: 1.5,
                    sourceStartSeconds: 2,
                },
            ],
        });
        expect(tracks[1]).toMatchObject({
            volume: 1.25,
            source: {
                kind: "AceStepFun",
                reference: "audio1",
                uploadedAudio: null,
            },
            spans: [
                {
                    id: "old-b",
                    timelineStartSeconds: 5,
                    timelineLengthSeconds: 2,
                    sourceStartSeconds: 0.5,
                },
            ],
        });
    });

    it("appends migrated overlays after already-authored root lanes", () => {
        const existing = [
            {
                id: "existing",
                volume: 1,
                source: {
                    kind: "AceStepFun" as const,
                    reference: "audio0",
                    uploadedAudio: null,
                },
                spans: [],
            },
        ];
        const clips = [
            minimalClip({
                duration: 2,
                audioSegments: [
                    {
                        source: null,
                        startSeconds: 0,
                        trimStartSeconds: 0,
                        lengthSeconds: 1,
                        volume: 1,
                    },
                ],
            }),
        ];

        const tracks = migrateClipAudioSegmentsToTimeline(clips, existing);

        expect(tracks[0]).toBe(existing[0]);
        expect(tracks).toHaveLength(2);
    });

    it("runs automatically when a v3 authoring document is decoded", () => {
        const serialized = JSON.stringify({
            schemaVersion: 3,
            clips: [
                minimalClip({
                    duration: 5,
                    audioSegments: [
                        {
                            source: "audio0",
                            startSeconds: 3,
                            trimStartSeconds: 1,
                            lengthSeconds: 2,
                            volume: 0.8,
                        },
                    ],
                }),
            ],
            audioTracks: [],
        });

        const decoded = decodeStoredDocument(serialized, {
            width: 512,
            height: 512,
            fps: 24,
        });

        expect(decoded?.clips[0].audioSegments).toEqual([]);
        expect(decoded?.audioTracks).toHaveLength(1);
        expect(decoded?.audioTracks?.[0]).toMatchObject({
            volume: 0.8,
            source: { kind: "AceStepFun", reference: "audio0" },
            spans: [
                {
                    timelineStartSeconds: 3,
                    timelineLengthSeconds: 2,
                    sourceStartSeconds: 1,
                },
            ],
        });
    });
});
