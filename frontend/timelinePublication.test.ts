import { describe, expect, it } from "@jest/globals";

import { minimalClip } from "./__test_helpers__/clipFixtures";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "./generatedMediaSource";
import { resolveTimelinePublication } from "./timelinePublication";

describe("resolveTimelinePublication", () => {
    it("keeps an audio-only clip as the standalone output", () => {
        expect(
            resolveTimelinePublication([
                minimalClip({ audioOnly: true, duration: 20 }),
            ]),
        ).toMatchObject({
            outputClipIndexes: [0],
            consumedClipIndexes: [],
            dependencies: [],
        });
    });

    it("uses a leading audio-only clip as the next clip's base audio", () => {
        expect(
            resolveTimelinePublication([
                minimalClip({ audioOnly: true, duration: 20 }),
                minimalClip({ audioSource: MEDIA_SOURCE_PREVIOUS_CLIP }),
            ]),
        ).toMatchObject({
            outputClipIndexes: [1],
            consumedClipIndexes: [0],
            dependencies: [
                {
                    sourceClipIdx: 0,
                    targetClipIdx: 1,
                    kind: "audio-base",
                },
            ],
        });
    });

    it("overrides the next clip to use leading audio as its base", () => {
        expect(
            resolveTimelinePublication([
                minimalClip({ audioOnly: true }),
                minimalClip({ audioSource: "Native" }),
            ]).dependencies,
        ).toEqual([
            {
                sourceClipIdx: 0,
                targetClipIdx: 1,
                kind: "audio-base",
            },
        ]);
    });

    it("does not route Clip 0 audio past its immediate successor", () => {
        expect(
            resolveTimelinePublication([
                minimalClip({ audioOnly: true }),
                minimalClip(),
                minimalClip({ audioSource: MEDIA_SOURCE_PREVIOUS_CLIP }),
            ]).dependencies,
        ).toEqual([
            {
                sourceClipIdx: 0,
                targetClipIdx: 1,
                kind: "audio-base",
            },
            {
                sourceClipIdx: 1,
                targetClipIdx: 2,
                kind: "audio-base",
            },
        ]);
    });

    it("replaces a published video with a clip that uses it as source", () => {
        expect(
            resolveTimelinePublication([
                minimalClip(),
                minimalClip({
                    initVideo: {
                        source: MEDIA_SOURCE_PREVIOUS_CLIP,
                        data: "",
                        fileName: null,
                        fps: 24,
                        durationSeconds: 2,
                        startSeconds: 0,
                        lengthSeconds: 2,
                    },
                }),
            ]),
        ).toMatchObject({
            outputClipIndexes: [1],
            consumedClipIndexes: [0],
            dependencies: [
                {
                    sourceClipIdx: 0,
                    targetClipIdx: 1,
                    kind: "video-source",
                },
            ],
        });
    });

    it("keeps ordinary clips as consecutive output segments", () => {
        expect(
            resolveTimelinePublication([minimalClip(), minimalClip()]),
        ).toMatchObject({
            outputClipIndexes: [0, 1],
            consumedClipIndexes: [],
            dependencies: [],
        });
    });
});
