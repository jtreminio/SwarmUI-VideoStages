import { describe, expect, it } from "@jest/globals";

import { minimalClip } from "./__test_helpers__/clipFixtures";
import { executableBoundaries } from "./clipSemantics";
import { detailBreadcrumb } from "./detailStrip/panelRouter";
import { computeRegionLayout } from "./timelineView/layout";
import { renderBoundarySeams } from "./timelineView/regionRenderer";
import type { Clip } from "./types";

// Active A, skipped B, active C: execution compiles the single A→C join, so the
// timeline must render exactly that seam — not the two raw-adjacency seams.
const clips = (): Clip[] => [
    minimalClip({ id: "a" }),
    minimalClip({ id: "b", skipped: true }),
    minimalClip({ id: "c" }),
];

describe("executable boundary seams", () => {
    it("compacts a skipped clip into one A→C descriptor", () => {
        expect(executableBoundaries(clips())).toEqual([
            {
                position: 0,
                leftIdx: 0,
                rightIdx: 2,
                leftId: "a",
                rightId: "c",
            },
        ]);
    });

    it("renders exactly one chip, anchored at the executable target clip", () => {
        const list = clips();
        const layouts = computeRegionLayout(list, { pxPerSecond: 10 });
        const host = document.createElement("div");
        host.innerHTML = renderBoundarySeams(list, layouts);
        const chips = Array.from(
            host.querySelectorAll<HTMLElement>("[data-vst-boundary-chip]"),
        );
        expect(chips).toHaveLength(1);
        expect(chips[0].getAttribute("data-left-clip-idx")).toBe("0");
        expect(chips[0].getAttribute("data-right-clip-idx")).toBe("2");
        expect(chips[0].style.left).toBe(`${layouts[2].startPx}px`);
        expect(chips[0].getAttribute("title")).toContain(
            "Boundary clip 0 → 2:",
        );
    });

    it("labels the dock breadcrumb with the executable neighbour", () => {
        expect(
            detailBreadcrumb({ kind: "boundary", leftClipIdx: 0 }, clips()),
        ).toBe("Boundary · Clip 0 → 2");
    });
});
