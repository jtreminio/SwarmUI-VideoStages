import { describe, expect, it } from "@jest/globals";
import {
    boundaryWindowChoices,
    boundaryWindowConstraints,
    normalizeBoundaryWindow,
} from "./boundaryConstraints";

describe("catalog boundary constraints", () => {
    it.each([
        ["reference", "reference"],
        ["overlap", "overlap"],
        [undefined, "overlap"],
        ["", "overlap"],
        ["Reference", "overlap"],
        [7, "overlap"],
        [{}, "overlap"],
    ])("resolves continueMode %p to %p", (authored, expected) => {
        expect(
            boundaryWindowConstraints({
                support: "conditional",
                code: "future.boundary.continue",
                reason: "Continue rule.",
                constraints: { continueMode: authored },
            }).continueMode,
        ).toBe(expected);
    });

    it("drives choices and normalization from a future architecture's grid", () => {
        const constraints = boundaryWindowConstraints({
            support: "conditional",
            code: "future.boundary.continue",
            reason: "Future architecture continuity.",
            constraints: {
                frameStep: 5,
                minFrames: 5,
                maxFrames: 20,
                defaultFrames: 10,
                continuityExtraFrames: 2,
            },
        });

        expect(boundaryWindowChoices(constraints)).toEqual([5, 10, 15, 20]);
        expect(normalizeBoundaryWindow(18, constraints)).toBe(15);
        expect(constraints.continuityExtraFrames).toBe(2);
    });

    it("snaps relative to a non-zero minimum", () => {
        const constraints = boundaryWindowConstraints({
            support: "conditional",
            code: "future.boundary.offset-grid",
            reason: "Offset frame grid.",
            constraints: {
                frameStep: 4,
                minFrames: 5,
                maxFrames: 17,
                defaultFrames: 5,
            },
        });

        expect(boundaryWindowChoices(constraints)).toEqual([5, 9, 13, 17]);
        expect(normalizeBoundaryWindow(14, constraints)).toBe(13);
    });
});
