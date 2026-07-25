import { describe, expect, it } from "@jest/globals";
import { keyframeLeftPercent, spanGeometry } from "./trackDomUtils";

describe("spanGeometry", () => {
    it("clamps a percentage span inside its lane by default", () => {
        // 4s + 3s in a 5s lane: the span ends at the lane edge, not past it.
        expect(spanGeometry(4, 3, 5)).toMatchObject({
            left: 80,
            width: 20,
            empty: false,
        });
        expect(spanGeometry(-2, 20, 5)).toMatchObject({ left: 0, width: 100 });
    });

    it("reports an empty span instead of a negative extent", () => {
        expect(spanGeometry(3, -1, 5)).toMatchObject({ width: 0, empty: true });
        expect(spanGeometry(1, 2, 0)).toMatchObject({ empty: true });
    });

    it("emits pixels with an optional minimum-width floor", () => {
        expect(
            spanGeometry(1, 2, 10, { unit: "px", pxPerSecond: 20 }),
        ).toMatchObject({ left: 20, width: 40 });
        expect(
            spanGeometry(1, 0, 10, {
                unit: "px",
                pxPerSecond: 20,
                minWidth: 2,
            }),
        ).toMatchObject({ left: 20, width: 2 });
    });

    it("allows the output clamp to be turned off", () => {
        expect(spanGeometry(4, 3, 5, { clampOutput: false })).toMatchObject({
            left: 80,
            width: 20,
        });
    });
});

describe("keyframeLeftPercent", () => {
    it("returns 0 for a zero/invalid duration", () => {
        expect(keyframeLeftPercent(2, 0)).toBe(0);
        expect(keyframeLeftPercent(2, Number.NaN)).toBe(0);
    });

    it("clamps to 100 when time exceeds duration", () => {
        expect(keyframeLeftPercent(10, 5)).toBe(100);
    });

    it("is proportional within the region", () => {
        expect(keyframeLeftPercent(2.5, 5)).toBe(50);
        expect(keyframeLeftPercent(0, 5)).toBe(0);
    });
});
