import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { setVideoStagesHostBridgeForTests } from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import { estimateFpsFromMediaTimes, probeInitVideo } from "./mediaProbe";

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

describe("probeInitVideo", () => {
    afterEach(() => setVideoStagesHostBridgeForTests(null));

    it("reports the source video's pixel dimensions", async () => {
        const video = document.createElement("video");
        Object.defineProperties(video, {
            duration: { value: 5.4 },
            videoWidth: { value: 1024 },
            videoHeight: { value: 1664 },
        });
        video.pause = jest.fn();
        video.load = jest.fn();
        const base = createDefaultVideoStagesHostBridge();
        setVideoStagesHostBridgeForTests({
            ...base,
            createInitVideoElement: () => {
                queueMicrotask(() =>
                    video.dispatchEvent(new Event("loadedmetadata")),
                );
                return video;
            },
        });

        await expect(
            probeInitVideo("data:video/mp4;base64,AA=="),
        ).resolves.toEqual({
            durationSeconds: 5.4,
            fps: null,
            width: 1024,
            height: 1664,
        });
    });
});
