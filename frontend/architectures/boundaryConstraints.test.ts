import { describe, expect, it } from "@jest/globals";
import {
    boundaryOverlapChoices,
    boundaryOverlapConstraints,
    normalizeBoundaryOverlap,
} from "./boundaryConstraints";

describe("catalog boundary constraints", () => {
    it("drives choices and normalization from a future architecture's grid", () => {
        const constraints = boundaryOverlapConstraints({
            support: "conditional",
            code: "future.boundary.continue",
            reason: "Future architecture continuity.",
            scope: "boundary",
            entityId: null,
            constraints: {
                frameStep: 5,
                minFrames: 5,
                maxFrames: 20,
                defaultFrames: 10,
                continuityExtraFrames: 2,
            },
        });

        expect(boundaryOverlapChoices(constraints)).toEqual([5, 10, 15, 20]);
        expect(normalizeBoundaryOverlap(18, constraints)).toBe(15);
        expect(constraints.continuityExtraFrames).toBe(2);
    });

    it("snaps relative to a non-zero minimum", () => {
        const constraints = boundaryOverlapConstraints({
            support: "conditional",
            code: "future.boundary.offset-grid",
            reason: "Offset frame grid.",
            scope: "boundary",
            entityId: null,
            constraints: {
                frameStep: 4,
                minFrames: 5,
                maxFrames: 17,
                defaultFrames: 5,
            },
        });

        expect(boundaryOverlapChoices(constraints)).toEqual([5, 9, 13, 17]);
        expect(normalizeBoundaryOverlap(14, constraints)).toBe(13);
    });
});
