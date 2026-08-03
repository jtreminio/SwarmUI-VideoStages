import { describe, expect, it } from "@jest/globals";
import { framesForClip } from "./renderUtils";

describe("framesForClip", () => {
    it("aligns duration frames up to a multiple of eight plus one", () => {
        expect(
            framesForClip(10, 24, { frameGrid: 8, frameGridOrigin: 1 }),
        ).toBe(241);
        expect(
            framesForClip(21.5, 24, { frameGrid: 8, frameGridOrigin: 1 }),
        ).toBe(521);
    });

    it("aligns any positive partial segment up to one frame block plus one", () => {
        expect(
            framesForClip(0.1, 4, { frameGrid: 8, frameGridOrigin: 1 }),
        ).toBe(9);
    });

    it("uses the resolved grid instead of a global eight-frame assumption", () => {
        expect(
            framesForClip(1.05, 24, { frameGrid: 1, frameGridOrigin: 1 }),
        ).toBe(27);
        expect(
            framesForClip(1.05, 24, { frameGrid: 6, frameGridOrigin: 1 }),
        ).toBe(31);
        expect(
            framesForClip(1.05, 24, { frameGrid: 8, frameGridOrigin: 1 }),
        ).toBe(33);
    });
});
