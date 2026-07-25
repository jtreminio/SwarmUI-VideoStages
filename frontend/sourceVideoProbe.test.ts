import { describe, expect, it } from "@jest/globals";
import { estimateFpsFromMediaTimes } from "./sourceVideoProbe";

const uniformTimes = (fps: number, count: number): number[] =>
    Array.from({ length: count }, (_, i) => i / fps);

describe("estimateFpsFromMediaTimes", () => {
    it("recovers a uniform frame rate", () => {
        expect(estimateFpsFromMediaTimes(uniformTimes(24, 12))).toBe(24);
        expect(estimateFpsFromMediaTimes(uniformTimes(30, 12))).toBe(30);
    });

    it("rounds near-NTSC rates to the whole fps", () => {
        expect(estimateFpsFromMediaTimes(uniformTimes(23.976, 12))).toBe(24);
        expect(estimateFpsFromMediaTimes(uniformTimes(29.97, 12))).toBe(30);
    });

    it("ignores duplicate presentation times (stalled frames)", () => {
        const times = [0, 1 / 24, 1 / 24, 2 / 24, 3 / 24, 4 / 24, 5 / 24];
        expect(estimateFpsFromMediaTimes(times)).toBe(24);
    });

    it("uses the median, so a single long stall does not skew the result", () => {
        const times = uniformTimes(24, 10);
        times.push(times[times.length - 1] + 1.5);
        expect(estimateFpsFromMediaTimes(times)).toBe(24);
    });

    it("returns null with too few samples or an implausible rate", () => {
        expect(estimateFpsFromMediaTimes([])).toBeNull();
        expect(estimateFpsFromMediaTimes(uniformTimes(24, 4))).toBeNull();
        expect(estimateFpsFromMediaTimes(uniformTimes(1500, 12))).toBeNull();
    });
});
