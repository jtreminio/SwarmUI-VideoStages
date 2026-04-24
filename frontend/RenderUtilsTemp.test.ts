import { describe, expect, it } from "@jest/globals";
import { framesForClip } from "./RenderUtilsTemp";

describe("framesForClip", () => {
    it("aligns duration frames up to a multiple of eight plus one", () => {
        expect(framesForClip(10, 24)).toBe(241);
        expect(framesForClip(21.5, 24)).toBe(521);
    });

    it("aligns any positive partial segment up to one frame block plus one", () => {
        expect(framesForClip(0.1, 4)).toBe(9);
    });
});
