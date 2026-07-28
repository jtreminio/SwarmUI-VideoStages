import { describe, expect, it } from "@jest/globals";

import { dimensionsFor, matchAspectRatio } from "./dimensionPresets";
import { snapDimensions } from "./dimensionSnap";

describe("dimension snapping", () => {
    it("passes exact multiples through and defaults to the global /32 grid", () => {
        expect(snapDimensions(1280, 704)).toEqual({
            width: 1280,
            height: 704,
        });
        expect(snapDimensions(638, 359)).toEqual({
            width: 640,
            height: 352,
        });
    });

    it("prefers the candidate with less aspect drift over blind flooring", () => {
        expect(snapDimensions(1232, 688, 64)).toEqual({
            width: 1280,
            height: 704,
        });
    });

    it("clamps candidates at the snap minimum and root maximum", () => {
        expect(snapDimensions(20, 10)).toEqual({
            width: 32,
            height: 32,
        });
        expect(snapDimensions(5000, 3000)).toEqual({
            width: 4096,
            height: 2976,
        });
    });

    it("resolves equal scores toward the larger area", () => {
        const midpoint = Math.sqrt(32 * 64);
        expect(snapDimensions(midpoint, midpoint)).toEqual({
            width: 64,
            height: 64,
        });
    });
});

describe("SwarmUI aspect-ratio vocabulary", () => {
    it("uses the host reference sheet and preserves its missing 3:4 fallback", () => {
        expect(dimensionsFor("16:9", 1024)).toEqual({
            width: 1344,
            height: 768,
        });
        expect(dimensionsFor("3:4", 1024)).toBeNull();
    });

    it("recognizes exact and emitted effective dimensions", () => {
        expect(matchAspectRatio(800, 600)).toBe("4:3");
        expect(matchAspectRatio(1408, 768, 128)).toBe("16:9");
        expect(matchAspectRatio(777, 555)).toBeNull();
    });
});
