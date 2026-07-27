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
        expect(timing.generatedFrames).toBe(146);
        expect(timing.joinFrames).toBe(25);
        expect(timing.outputFrames).toBe(121);
        expect(timing.outputSeconds).toBeCloseTo(121 / 24);
        expect(timing.boundaries[0]).toMatchObject({
            leftIdx: 0,
            rightIdx: 1,
            requestedMode: "continue",
            effectiveMode: "continue",
            overlapFrames: 25,
        });
        const edges = timelineClipEdges(clips, timing);
        expect(edges).toHaveLength(5);
        expect(edges[0]).toBeCloseTo(0);
        expect(edges[1]).toBeCloseTo(2);
        expect(edges[2]).toBeCloseTo(121 / 48);
        expect(edges[3]).toBeCloseTo(73 / 24);
        expect(edges[4]).toBeCloseTo(121 / 24);
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
