import { describe, expect, it } from "@jest/globals";

import {
    fromInOut,
    setInPoint,
    setOutPoint,
    slideRange,
    toInOut,
} from "./trimGeometry";

const LIMITS = { limitSeconds: 12.4, minLengthSeconds: 1, fps: 30 };

describe("trim geometry", () => {
    it("reads a stored start/length as the in and out the user sees", () => {
        expect(toInOut({ startSeconds: 2.1, lengthSeconds: 4.2 })).toEqual({
            inSeconds: 2.1,
            outSeconds: 6.3,
        });
    });

    it("moves the in point and holds the out point, shortening the range", () => {
        expect(
            setInPoint({ startSeconds: 2.1, lengthSeconds: 4.2 }, 3.3, LIMITS),
        ).toEqual({ startSeconds: 3.3, lengthSeconds: 3 });
    });

    it("moves the out point and holds the in point", () => {
        expect(
            setOutPoint({ startSeconds: 2.1, lengthSeconds: 4.2 }, 8.1, LIMITS),
        ).toEqual({ startSeconds: 2.1, lengthSeconds: 6 });
    });

    /**
     * The in point crossing the out point is the gesture that would otherwise
     * invert the range; it must pin against the minimum, never swap the edges.
     */
    it("stops the in point a minimum length short of the out point", () => {
        expect(
            setInPoint({ startSeconds: 2, lengthSeconds: 4 }, 9, LIMITS),
        ).toEqual({ startSeconds: 5, lengthSeconds: 1 });
    });

    it("stops the out point a minimum length past the in point", () => {
        expect(
            setOutPoint({ startSeconds: 2, lengthSeconds: 4 }, 0.5, LIMITS),
        ).toEqual({ startSeconds: 2, lengthSeconds: 1 });
    });

    it("holds the out point at the end of the source", () => {
        expect(
            setOutPoint({ startSeconds: 2, lengthSeconds: 4 }, 99, LIMITS),
        ).toEqual({ startSeconds: 2, lengthSeconds: 10.4 });
    });

    /**
     * Sliding is the behavior the old "Start (s)" field had. It survives as the
     * bar's middle drag, so the length must come through untouched.
     */
    it("slides the whole window without changing its length", () => {
        expect(
            slideRange({ startSeconds: 2, lengthSeconds: 4 }, 6, LIMITS),
        ).toEqual({ startSeconds: 6, lengthSeconds: 4 });
    });

    it("stops a slide at the end of the source instead of shortening it", () => {
        expect(
            slideRange({ startSeconds: 2, lengthSeconds: 4 }, 99, LIMITS),
        ).toEqual({ startSeconds: 8.4, lengthSeconds: 4 });
    });

    /**
     * The clip inherits this length as its duration. Left unsnapped it draws a
     * clip on the timeline that disagrees with the number in the box, which is
     * the drift `applyClipDurationResize` used to leave behind.
     */
    it("snaps the length onto the fps grid", () => {
        expect(fromInOut({ inSeconds: 0, outSeconds: 4.03 }, LIMITS)).toEqual({
            startSeconds: 0,
            lengthSeconds: 4,
        });
    });

    it("leaves the length unsnapped when the fps is unknown", () => {
        expect(
            fromInOut(
                { inSeconds: 0, outSeconds: 4.03 },
                { ...LIMITS, fps: 0 },
            ),
        ).toEqual({ startSeconds: 0, lengthSeconds: 4 });
    });

    /**
     * Snapping rounds the length up, so a range already touching the end of the
     * source would reach past it if the clamp did not come after the snap.
     */
    it("keeps a snapped range inside the source it came from", () => {
        const trimmed = fromInOut(
            { inSeconds: 0, outSeconds: 12.4 },
            { limitSeconds: 12.4, minLengthSeconds: 1, fps: 7 },
        );
        expect(
            trimmed.startSeconds + trimmed.lengthSeconds,
        ).toBeLessThanOrEqual(12.4);
    });
});
