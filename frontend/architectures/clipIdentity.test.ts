import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "../__test_helpers__/architectureFixtures";
import { minimalClip, minimalStage } from "../__test_helpers__/clipFixtures";
import {
    deriveClipArchitectureIdentity,
    reconcileClipArchitectureIdentity,
} from "./clipIdentity";

describe("clip architecture identity", () => {
    it("derives authored Stage 0 separately from a source-only effective identity", () => {
        const catalog = testArchitectureCatalog();
        const clip = minimalClip({
            architecture: "none",
            modelProfileId: "none",
            sourceVideo: {
                data: "data:video/mp4;base64,AAAA",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [minimalStage({ skipped: true })],
        });

        expect(deriveClipArchitectureIdentity(clip, catalog)).toEqual({
            architectureId: "none",
            modelProfileId: "none",
            authoredArchitectureId: "ltx2",
            authoredModelProfileId: "ltx-2.3",
        });
    });

    it("reconciles a sourced clip to its effective source-only identity", () => {
        const catalog = testArchitectureCatalog();
        const clip = minimalClip({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
            sourceVideo: {
                data: "data:video/mp4;base64,AAAA",
                fileName: "source.mp4",
                fps: 24,
                durationSeconds: 2,
                startSeconds: 0,
                lengthSeconds: 2,
            },
            stages: [minimalStage({ skipped: true })],
        });

        expect(reconcileClipArchitectureIdentity(clip, catalog)).toBe(true);
        expect(clip).toMatchObject({
            architecture: "none",
            modelProfileId: "none",
        });
    });
});
