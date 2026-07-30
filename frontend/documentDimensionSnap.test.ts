import { describe, expect, it } from "@jest/globals";

import {
    activeDocumentDimensionMultiple,
    snapExplicitDocumentDimensions,
} from "./documentDimensionSnap";
import type { Clip, VideoStagesConfig } from "./types";

const clip = (architecture: string): Clip =>
    ({
        architectureHint: architecture,
        sourceVideo: null,
        stages: [],
        icLoras: [],
    }) as unknown as Clip;

describe("document dimension policy", () => {
    it("applies the global /32 grid to architectures without extra policy", () => {
        expect(
            activeDocumentDimensionMultiple([clip("future-video")], null),
        ).toBe(32);

        const state = {
            width: 638,
            height: 359,
            dimsExplicit: true,
            clips: [clip("future-video")],
        } as VideoStagesConfig;
        expect(snapExplicitDocumentDimensions(state, null)).toMatchObject({
            changed: true,
            multiple: 32,
            before: { width: 638, height: 359 },
            after: { width: 640, height: 352 },
        });
    });

    it("does not rewrite inherited host dimensions merely because they were read", () => {
        const state = {
            width: 1232,
            height: 688,
            dimsExplicit: false,
            clips: [clip("future-video")],
        } as VideoStagesConfig;
        expect(snapExplicitDocumentDimensions(state, null).changed).toBe(false);
        expect(state).toMatchObject({ width: 1232, height: 688 });
    });
});
