import { describe, expect, it } from "@jest/globals";

import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { minimalClip, minimalStage } from "./__test_helpers__/clipFixtures";
import { createCapabilityViewResolver } from "./architectures/policy";
import { activeStageCount, executableClipIndexes } from "./clipSemantics";
import { timelineClipEdges } from "./timelineSnap";
import { resolveTimelineTiming } from "./timelineTiming";

const capabilities = () =>
    createCapabilityViewResolver(testArchitectureCatalog());

describe("resolveTimelineTiming", () => {
    it("uses LTX's effective 25-frame Continue window for a selected 24 frames", () => {
        const clips = [
            minimalClip({
                duration: 3,
                boundaryOut: "continue",
                boundaryOutOverlap: 24,
            }),
            minimalClip({ duration: 3 }),
        ];

        const timing = resolveTimelineTiming(clips, 24, capabilities());

        expect(timing.authoredSeconds).toBe(6);
        expect(timing.generatedFrames).toBe(170);
        expect(timing.joinFrames).toBe(25);
        expect(timing.outputFrames).toBe(145);
        expect(timing.outputSeconds).toBeCloseTo(145 / 24);
        expect(timing.boundaries[0]).toMatchObject({
            leftIdx: 0,
            rightIdx: 1,
            requestedMode: "continue",
            effectiveMode: "continue",
            overlapFrames: 25,
            handleFrames: 24,
            timelineReductionFrames: 1,
        });
        const edges = timelineClipEdges(clips, timing);
        expect(edges).toHaveLength(4);
        expect(edges[0]).toBeCloseTo(0);
        expect(edges[1]).toBeCloseTo(47 / 24);
        expect(edges[2]).toBeCloseTo(3);
        expect(edges[3]).toBeCloseTo(145 / 24);
    });

    it("uses the selected 24-frame Crossfade without a continuity extra", () => {
        const clips = [
            minimalClip({
                duration: 3,
                boundaryOut: "crossfade",
                boundaryOutOverlap: 24,
            }),
            minimalClip({ duration: 3 }),
        ];

        const timing = resolveTimelineTiming(clips, 24, capabilities());

        expect(timing.generatedFrames).toBe(146);
        expect(timing.joinFrames).toBe(24);
        expect(timing.outputFrames).toBe(122);
    });

    it("keeps two authored five-second clips at a 241-frame output", () => {
        const clips = [
            minimalClip({
                duration: 5,
                boundaryOut: "continue",
                boundaryOutOverlap: 24,
            }),
            minimalClip({ duration: 5 }),
        ];

        const timing = resolveTimelineTiming(clips, 24, capabilities());

        expect(timing.clipFrames).toEqual([121, 121]);
        expect(timing.generatedFrames).toBe(266);
        expect(timing.joinFrames).toBe(25);
        expect(timing.outputFrames).toBe(241);
    });

    it("falls Continue back to Cut for a runtime-derived target length", () => {
        const clips = [
            minimalClip({ duration: 3, boundaryOut: "continue" }),
            minimalClip({ duration: 3, clipLengthFromAudio: true }),
        ];

        const timing = resolveTimelineTiming(clips, 24, capabilities());

        expect(timing.boundaries[0].effectiveMode).toBe("cut");
        expect(timing.boundaries[0].handleFrames).toBe(0);
        expect(timing.generatedFrames).toBe(146);
        expect(timing.outputFrames).toBe(146);
    });

    it("truncates clips at the first skip marker", () => {
        const clips = [
            minimalClip({ duration: 2 }),
            minimalClip({ duration: 3, skipped: true }),
            minimalClip({ duration: 4 }),
        ];

        expect(executableClipIndexes(clips)).toEqual([0]);
        const timing = resolveTimelineTiming(clips, 24, capabilities());
        expect(timing.authoredSeconds).toBe(2);
        expect(timing.generatedFrames).toBe(49);
        expect(timing.outputFrames).toBe(49);
        expect(timing.outputGeometryAvailable).toBe(false);
    });

    it("truncates stages at the first skip marker", () => {
        const clip = minimalClip({
            stages: [
                minimalStage(),
                minimalStage({ skipped: true }),
                minimalStage(),
            ],
        });

        expect(activeStageCount(clip)).toBe(1);
        expect(executableClipIndexes([clip])).toEqual([0]);
    });
});
