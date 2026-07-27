import { describe, expect, it } from "@jest/globals";

import { minimalClip } from "./__test_helpers__/clipFixtures";
import { executableBoundaries } from "./clipSemantics";
import { detailBreadcrumb } from "./detailStrip/panelRouter";
import { computeRegionLayout } from "./timelineView/layout";
import { renderBoundarySeams } from "./timelineView/regionRenderer";
import type { Clip } from "./types";

// Active A, skipped B, authored C: B is a truncation marker, so execution ends
// after A and no later boundary exists.
const clips = (): Clip[] => [
    minimalClip({ id: "a" }),
    minimalClip({ id: "b", skipped: true }),
    minimalClip({ id: "c" }),
];

describe("executable boundary seams", () => {
    it("truncates boundaries at the first skipped clip", () => {
        expect(executableBoundaries(clips())).toEqual([]);
    });

    it("renders no chip beyond the truncation point", () => {
        const list = clips();
        const layouts = computeRegionLayout(list, { pxPerSecond: 10 });
        const host = document.createElement("div");
        host.innerHTML = renderBoundarySeams(list, layouts);
        const chips = Array.from(
            host.querySelectorAll<HTMLElement>("[data-vst-boundary-chip]"),
        );
        expect(chips).toHaveLength(0);
    });

    it("labels the final executable clip as the end", () => {
        expect(
            detailBreadcrumb({ kind: "boundary", leftClipIdx: 0 }, clips()),
        ).toBe("Boundary · Clip 0 → end");
    });
});
