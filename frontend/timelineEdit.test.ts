import { describe, expect, it } from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import {
    applyClipDurationResize,
    clampClipRefsToDuration,
    pxToDuration,
    pxToFrame,
} from "./timelineEdit";
import type { Clip, FrameRefImage, RootDefaults } from "./types";

// Minimal RootDefaults stub — only fps/frames are read by getReferenceFrameMax.
const rootDefaults = (fps: number, frames = 24): RootDefaults =>
    ({
        fps,
        frames,
        modelCatalog: testArchitectureCatalog(),
    }) as unknown as RootDefaults;

const ref = (frame: number): FrameRefImage =>
    ({
        source: "refiner",
        uploadFileName: null,
        uploadedImage: null,
        frame,
        fromEnd: false,
    }) as FrameRefImage;

const clip = (duration: number, frameRefs: FrameRefImage[] = []): Clip =>
    ({
        duration,
        frameRefs,
        stages: [{ model: "ltx", skipped: false }],
    }) as unknown as Clip;

describe("pxToDuration", () => {
    it("converts px to seconds at the given scale", () => {
        // 88px / 44px-per-second = 2.0s, which snaps cleanly to 2 on a 24fps grid.
        expect(pxToDuration(88, 44, 24)).toBe(2);
    });

    it("snaps to the fps frame grid", () => {
        // 80px / 44 = 1.818s → 44 frames @24fps → 1.833s → floored to 1.8.
        expect(pxToDuration(80, 44, 24)).toBe(1.8);
    });

    it("floors at the clip duration minimum", () => {
        // 10px / 44 = 0.227s, well under the 1s minimum.
        expect(pxToDuration(10, 44, 24)).toBe(1);
        expect(pxToDuration(0, 44, 24)).toBe(1);
        expect(pxToDuration(-50, 44, 24)).toBe(1);
    });

    it("collapses degenerate scale/px to the minimum", () => {
        expect(pxToDuration(100, 0, 24)).toBe(1);
        expect(pxToDuration(Number.NaN, 44, 24)).toBe(1);
        expect(pxToDuration(100, Number.NaN, 24)).toBe(1);
    });
});

describe("pxToFrame", () => {
    // duration 5s @24fps: framesForClip = ceil(120/8)*8+1 = 121 → the frame max both ends clamp to.
    const DUR = 5;
    const FPS = 24;
    const WIDTH = 100;

    it("maps x across the region to frame = round(time*fps) from the start", () => {
        expect(
            pxToFrame(50, WIDTH, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(61);
        expect(
            pxToFrame(25, WIDTH, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(30); // quarter → 1.25s → 30f
    });

    it("measures from the clip end when fromEnd", () => {
        // Same x, but the frame is the distance from the end: 50% → 2.5s from end → 60f.
        expect(
            pxToFrame(50, WIDTH, DUR, FPS, true, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(61);
        // Region left edge with fromEnd = the full clip away from the end.
        expect(
            pxToFrame(0, WIDTH, DUR, FPS, true, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(121);
    });

    it("clamps at the low end to REF_FRAME_MIN", () => {
        // Region left edge, start-relative → frame 0 → floored to REF_FRAME_MIN (1).
        expect(
            pxToFrame(0, WIDTH, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(1);
        // Region right edge, fromEnd → 0 frames from the end → floored to REF_FRAME_MIN (1).
        expect(
            pxToFrame(WIDTH, WIDTH, DUR, FPS, true, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(1);
        expect(
            pxToFrame(-40, WIDTH, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(1);
    });

    it("clamps at the high end to framesForClip's max", () => {
        expect(
            pxToFrame(WIDTH, WIDTH, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(121);
        expect(
            pxToFrame(400, WIDTH, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(121);
        expect(
            pxToFrame(WIDTH, WIDTH, 1.05, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(33);
    });

    it("collapses degenerate width/px to REF_FRAME_MIN", () => {
        expect(
            pxToFrame(50, 0, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(1);
        expect(
            pxToFrame(Number.NaN, WIDTH, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(1);
        expect(
            pxToFrame(50, Number.NaN, DUR, FPS, false, {
                frameGrid: 8,
                frameGridOrigin: 1,
            }),
        ).toBe(1);
    });
});

describe("clampClipRefsToDuration", () => {
    it("clamps ref frames past the new duration's max down to the max", () => {
        const c = clip(1.8, [ref(1000), ref(10), ref(-5)]);
        clampClipRefsToDuration(c, rootDefaults(24));
        expect(c.frameRefs[0].frame).toBe(49);
        expect(c.frameRefs[1].frame).toBe(10);
        expect(c.frameRefs[2].frame).toBe(1); // floored to REF_FRAME_MIN
    });
});

describe("applyClipDurationResize", () => {
    it("sets the duration, clamps frameRefs, and reports a change", () => {
        const c = clip(5, [ref(1000)]);
        const changed = applyClipDurationResize(c, 1.8, rootDefaults(24));
        expect(changed).toBe(true);
        expect(c.duration).toBe(1.8);
        expect(c.frameRefs[0].frame).toBe(49);
    });

    it("is a no-op when the duration is unchanged (no write signalled)", () => {
        const c = clip(2, [ref(1000)]);
        const changed = applyClipDurationResize(c, 2, rootDefaults(24));
        expect(changed).toBe(false);
        // Refs are left untouched on a no-op — the caller skips the write entirely.
        expect(c.frameRefs[0].frame).toBe(1000);
    });
});
