import { describe, expect, it } from "@jest/globals";
import {
    computeDropIndex,
    type DropRegion,
    finalIndexAfterMove,
    isNoOpMove,
    moveItem,
    remapIndexAfterReorder,
} from "./timelineReorder";

// midpoints 50/150/250
const REGIONS: DropRegion[] = [
    { startPx: 0, widthPx: 100 },
    { startPx: 100, widthPx: 100 },
    { startPx: 200, widthPx: 100 },
];

describe("computeDropIndex", () => {
    it("claims a region's leading half for the gap before it, trailing half for the gap after", () => {
        expect(computeDropIndex(10, REGIONS)).toBe(0); // left half of region 0
        expect(computeDropIndex(60, REGIONS)).toBe(1); // right half of region 0
        expect(computeDropIndex(140, REGIONS)).toBe(1); // left half of region 1
        expect(computeDropIndex(160, REGIONS)).toBe(2); // right half of region 1
        expect(computeDropIndex(240, REGIONS)).toBe(2); // left half of region 2
    });

    it("clamps to the ends", () => {
        expect(computeDropIndex(-9999, REGIONS)).toBe(0);
        expect(computeDropIndex(9999, REGIONS)).toBe(REGIONS.length);
    });

    it("returns 0 for an empty layout", () => {
        expect(computeDropIndex(123, [])).toBe(0);
    });

    it("treats the exact midpoint as the trailing gap", () => {
        expect(computeDropIndex(50, REGIONS)).toBe(1);
    });
});

describe("moveItem", () => {
    it("moves an element to a later gap (removal shift applied)", () => {
        // region 0 → gap after region 1 (gap index 2)
        expect(moveItem(["a", "b", "c", "d"], 0, 2)).toEqual([
            "b",
            "a",
            "c",
            "d",
        ]);
    });

    it("moves an element to an earlier gap", () => {
        expect(moveItem(["a", "b", "c", "d"], 3, 1)).toEqual([
            "a",
            "d",
            "b",
            "c",
        ]);
    });

    it("is a no-op when dropping into the same slot", () => {
        expect(moveItem(["a", "b", "c"], 1, 1)).toEqual(["a", "b", "c"]);
        expect(moveItem(["a", "b", "c"], 1, 2)).toEqual(["a", "b", "c"]);
    });

    it("clamps the destination at both ends", () => {
        expect(moveItem(["a", "b", "c"], 0, 99)).toEqual(["b", "c", "a"]);
        expect(moveItem(["a", "b", "c"], 2, -5)).toEqual(["c", "a", "b"]);
    });

    it("returns an unchanged copy for out-of-range / non-integer source", () => {
        const input = ["a", "b", "c"];
        expect(moveItem(input, 5, 0)).toEqual(input);
        expect(moveItem(input, -1, 0)).toEqual(input);
        expect(moveItem(input, 1.5, 0)).toEqual(input);
    });

    it("never mutates the input array", () => {
        const input = ["a", "b", "c"];
        const out = moveItem(input, 0, 2);
        expect(input).toEqual(["a", "b", "c"]);
        expect(out).not.toBe(input);
    });
});

describe("finalIndexAfterMove", () => {
    it("shifts left by one when moving to a later gap", () => {
        expect(finalIndexAfterMove(0, 2)).toBe(1);
        expect(finalIndexAfterMove(1, 3)).toBe(2);
    });

    it("keeps the gap index when moving to an earlier gap", () => {
        expect(finalIndexAfterMove(3, 1)).toBe(1);
        expect(finalIndexAfterMove(2, 0)).toBe(0);
    });
});

describe("isNoOpMove", () => {
    it("flags same-slot drops", () => {
        expect(isNoOpMove(1, 1)).toBe(true);
        expect(isNoOpMove(1, 2)).toBe(true);
    });

    it("flags genuine moves as non-trivial", () => {
        expect(isNoOpMove(0, 2)).toBe(false);
        expect(isNoOpMove(3, 1)).toBe(false);
    });
});

describe("remapIndexAfterReorder", () => {
    // Oracle: derive each index's remap from the array moveItem actually produces.
    const forEachIndex = (from: number, dest: number, n: number): number[] => {
        const before = Array.from({ length: n }, (_, i) => i);
        // Recover the gap that lands the moved element at `dest`.
        const gap = dest >= from ? dest + 1 : dest;
        const after = moveItem(before, from, gap);
        return before.map((i) => after.indexOf(i));
    };

    it("maps the moved element to its destination when moving right", () => {
        // [0,1,2,3] move 0→2 ⇒ [1,2,0,3]; old 0→2, old1→0, old2→1, old3→3.
        expect(
            [0, 1, 2, 3].map((i) => remapIndexAfterReorder(i, 0, 2)),
        ).toEqual(forEachIndex(0, 2, 4));
    });

    it("maps the moved element to its destination when moving left", () => {
        // [0,1,2,3] move 3→1 ⇒ [0,3,1,2]; old3→1, old1→2, old2→3, old0→0.
        expect(
            [0, 1, 2, 3].map((i) => remapIndexAfterReorder(i, 3, 1)),
        ).toEqual(forEachIndex(3, 1, 4));
    });

    it("leaves indices untouched when source equals destination", () => {
        expect([0, 1, 2].map((i) => remapIndexAfterReorder(i, 1, 1))).toEqual([
            0, 1, 2,
        ]);
    });
});
