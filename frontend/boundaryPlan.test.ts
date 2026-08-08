import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { boundaryWindowConstraints } from "./architectures/boundaryConstraints";
import { boundaryPlanForClips } from "./boundaryPlan";
import type { BoundaryOut, Clip } from "./types";

const ltxBoundaryConstraints = (
    _clip: Clip,
    _index: number,
    mode: BoundaryOut,
) =>
    boundaryWindowConstraints(
        testArchitectureCatalog().architectures[0].boundaryRules[mode],
    );

const clipFor = (boundaryOut: BoundaryOut, duration = 2): Clip =>
    ({ duration, boundaryOut, stages: [], frameRefs: [] }) as unknown as Clip;

describe("boundaryPlanForClips", () => {
    it("reports no overlap when every boundary is a cut", () => {
        const plan = boundaryPlanForClips(
            [clipFor("cut"), clipFor("cut")],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan).toEqual({
            overlaps: [0],
            continuityWindows: [0],
            fallback: false,
        });
    });

    it("resolves a continue boundary to the requested overlap + 1", () => {
        // Default overlap 8 -> window 9 (8n+1), ample for 2s @ 24fps clips.
        const plan = boundaryPlanForClips(
            [clipFor("continue"), clipFor("cut")],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan.overlaps[0]).toBe(9);
        expect(plan.fallback).toBe(false);
    });

    it("keeps reference continuation out of overlap accounting", () => {
        const plan = boundaryPlanForClips(
            [clipFor("continue"), clipFor("cut")],
            24,
            (_clip, _index, mode) => ({
                ...ltxBoundaryConstraints(_clip, _index, mode),
                continueMode: "reference",
                continuityExtraFrames: 0,
            }),
        );

        expect(plan.overlaps).toEqual([0]);
        expect(plan.continuityWindows).toEqual([8]);
        expect(plan.fallback).toBe(false);
    });

    it("does not let reference context fund a neighboring crossfade", () => {
        const plan = boundaryPlanForClips(
            [
                clipFor("continue", 2),
                clipFor("crossfade", 0),
                clipFor("cut", 2),
            ],
            24,
            (clip, index, mode) => ({
                ...ltxBoundaryConstraints(clip, index, mode),
                continueMode: mode === "continue" ? "reference" : "overlap",
                continuityExtraFrames: mode === "continue" ? 0 : 1,
            }),
        );

        expect(plan.overlaps).toEqual([0, 0]);
        expect(plan.continuityWindows).toEqual([8, 0]);
        expect(plan.fallback).toBe(false);
    });

    it("clamps the shared crossfade window to 8 for ample clips", () => {
        // 2s @ 24fps -> ceil(48/8)*8+1 = 49 frames; budget 48 >> 8.
        const plan = boundaryPlanForClips(
            [clipFor("crossfade"), clipFor("cut")],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan.overlaps[0]).toBe(8);
    });

    it("falls back to a cut when a clip is too short for the overlap", () => {
        // duration 0 -> budget 0, unreachable with real 8-frame-aligned durations;
        // guard still mirrors backend math.
        const plan = boundaryPlanForClips(
            [clipFor("crossfade", 0), clipFor("cut", 2)],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan.fallback).toBe(true);
        expect(plan.overlaps).toEqual([0]);
    });
});
