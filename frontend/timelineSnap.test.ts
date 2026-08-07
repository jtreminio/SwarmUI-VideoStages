import { describe, expect, it } from "@jest/globals";

import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { minimalClip } from "./__test_helpers__/clipFixtures";
import { createCapabilityViewResolver } from "./architectures/policy";
import { timelineClipEdges } from "./timelineSnap";
import { resolveTimelineTiming } from "./timelineTiming";

describe("timelineClipEdges", () => {
    it("puts the seam mid-crossfade with an edge at each end of the overlap", () => {
        const clips = [
            minimalClip({
                duration: 3,
                boundaryOut: "crossfade",
                boundaryOutOverlap: 24,
            }),
            minimalClip({ duration: 3 }),
        ];
        const timing = resolveTimelineTiming(
            clips,
            24,
            createCapabilityViewResolver(testArchitectureCatalog()),
        );

        const seam = 73 / 24 - 0.5;
        const edges = timelineClipEdges(clips, timing);

        expect(edges).toHaveLength(5);
        expect(edges[0]).toBeCloseTo(0);
        expect(edges[1]).toBeCloseTo(seam - 0.5);
        expect(edges[2]).toBeCloseTo(seam);
        expect(edges[3]).toBeCloseTo(seam + 0.5);
        expect(edges[4]).toBeCloseTo(timing.outputSeconds);
    });
});
