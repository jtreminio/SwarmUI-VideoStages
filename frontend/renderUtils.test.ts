import { describe, expect, it } from "@jest/globals";
import { framesForClip } from "./renderUtils";

describe("framesForClip", () => {
    it("aligns duration frames up to a multiple of eight plus one", () => {
        expect(framesForClip(10, 24, 8)).toBe(241);
        expect(framesForClip(21.5, 24, 8)).toBe(521);
    });

    it("aligns any positive partial segment up to one frame block plus one", () => {
        expect(framesForClip(0.1, 4, 8)).toBe(9);
    });

    it("uses the resolved grid instead of a global eight-frame assumption", () => {
        expect(framesForClip(1.05, 24, 1)).toBe(27);
        expect(framesForClip(1.05, 24, 6)).toBe(31);
        expect(framesForClip(1.05, 24, 8)).toBe(33);
    });
});
