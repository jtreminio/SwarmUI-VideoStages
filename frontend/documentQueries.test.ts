import { describe, expect, it } from "@jest/globals";
import { documentFps } from "./documentQueries";

describe("documentFps", () => {
    it("reads a valid canonical document FPS", () => {
        expect(documentFps({ fps: 30 })).toBe(30);
    });

    it("uses the shared 24fps fallback for an invalid document value", () => {
        expect(documentFps({ fps: 0 })).toBe(24);
        expect(documentFps({ fps: Number.NaN })).toBe(24);
    });
});
