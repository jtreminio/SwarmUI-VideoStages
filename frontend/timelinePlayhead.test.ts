import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import * as persistence from "./persistence";
import {
    createTimelinePlayhead,
    type TimelinePlayhead,
} from "./timelinePlayhead";
import {
    clipIndexAtSeconds,
    computeRegionLayout,
    formatPlayheadReadout,
    renderTimeline,
    resolvePlayheadSeconds,
    snapSecondsToFrame,
} from "./timelineView";
import type { Clip } from "./types";

const HEADER_W = 168;
const PPS = 44;
const FPS = 24;

describe("playhead pure helpers", () => {
    it("snapSecondsToFrame snaps to the nearest whole frame", () => {
        expect(snapSecondsToFrame(1.02, 24)).toBeCloseTo(1.0, 5);
        expect(snapSecondsToFrame(1.06, 10)).toBeCloseTo(1.1, 5);
        // Degenerate fps leaves the value non-negative and unsnapped.
        expect(snapSecondsToFrame(2.5, 0)).toBe(2.5);
        expect(snapSecondsToFrame(Number.NaN, 24)).toBe(0);
    });

    it("resolvePlayheadSeconds clamps to [0,total] then snaps", () => {
        expect(resolvePlayheadSeconds(-3, 10, 24)).toBe(0);
        expect(resolvePlayheadSeconds(99, 10, 24)).toBe(10);
        expect(resolvePlayheadSeconds(3.02, 10, 24)).toBeCloseTo(3.0, 5);
    });

    it("clipIndexAtSeconds resolves a seam to the right/starting clip", () => {
        const layouts = computeRegionLayout([clip(5), clip(5)], {
            pxPerSecond: PPS,
        });
        expect(clipIndexAtSeconds(layouts, 0)).toBe(0);
        expect(clipIndexAtSeconds(layouts, 4.9)).toBe(0);
        // Exactly on the seam belongs to clip 1.
        expect(clipIndexAtSeconds(layouts, 5)).toBe(1);
        expect(clipIndexAtSeconds(layouts, 9.9)).toBe(1);
        // Past the end stays on the last clip.
        expect(clipIndexAtSeconds(layouts, 12)).toBe(1);
        expect(clipIndexAtSeconds([], 3)).toBeNull();
    });

    it("formatPlayheadReadout shows seconds + frame at fps", () => {
        expect(formatPlayheadReadout(3.2, 24)).toBe("▸ 3.2s · f77");
        expect(formatPlayheadReadout(0, 24)).toBe("▸ 0.0s · f0");
    });
});

const clip = (duration: number): Clip =>
    ({ duration, stages: [{}], refs: [] }) as unknown as Clip;

const clipRecord = (duration: number): Record<string, unknown> => ({
    duration,
    stages: [{}],
    refs: [],
    promptWindows: [],
});

describe("playhead rendering", () => {
    let body: HTMLElement;

    beforeEach(() => {
        body = document.createElement("div");
        document.body.appendChild(body);
    });

    afterEach(() => {
        document.body.innerHTML = "";
    });

    it("renders the line + ruler handle at the resolved position", () => {
        renderTimeline(body, [clip(5), clip(5)], {
            fps: FPS,
            pxPerSecond: PPS,
            playheadSeconds: 3,
        });
        const line = body.querySelector<HTMLElement>("[data-vst-playhead]");
        const handle = body.querySelector<HTMLElement>(
            "[data-vst-playhead-handle]",
        );
        // Plane coord includes the sticky header column; ruler coord does not.
        expect(line?.style.left).toBe(`${HEADER_W + 3 * PPS}px`);
        expect(handle?.style.left).toBe(`${3 * PPS}px`);
        // Hit area exists and the line has no pointer events wrapper of its own.
        expect(body.querySelector("[data-vst-playhead-hit]")).not.toBeNull();
    });

    it("shows the playhead readout chip and marks the clip under the head", () => {
        renderTimeline(body, [clip(5), clip(5)], {
            fps: FPS,
            pxPerSecond: PPS,
            playheadSeconds: 3,
        });
        expect(body.querySelector("[data-vst-readout-head]")?.textContent).toBe(
            "▸ 3.0s · f72",
        );
        expect(
            body
                .querySelector('.vst-region[data-clip-idx="0"]')
                ?.classList.contains("vst-under-head"),
        ).toBe(true);
        expect(
            body
                .querySelector('.vst-region[data-clip-idx="1"]')
                ?.classList.contains("vst-under-head"),
        ).toBe(false);
    });

    it("resolves a head on a seam to the starting clip at render time", () => {
        renderTimeline(body, [clip(5), clip(5)], {
            fps: FPS,
            pxPerSecond: PPS,
            playheadSeconds: 5,
        });
        expect(
            body
                .querySelector('.vst-region[data-clip-idx="1"]')
                ?.classList.contains("vst-under-head"),
        ).toBe(true);
    });
});

describe("createTimelinePlayhead scrubbing", () => {
    let body: HTMLElement;
    let track: TimelinePlayhead | null = null;
    let committed: number[] = [];
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    const render = (): void => {
        const clips = persistence.getClips();
        renderTimeline(body, clips, {
            fps: FPS,
            pxPerSecond: PPS,
            playheadSeconds: committed.length
                ? committed[committed.length - 1]
                : 0,
        });
        const scroll = body.querySelector<HTMLElement>(".vst-scroll");
        if (scroll) {
            // jsdom has no layout; pin the scroll origin so clientX maps directly.
            scroll.getBoundingClientRect = () =>
                ({ left: 0, top: 0, right: 0, bottom: 0 }) as DOMRect;
        }
    };

    beforeEach(() => {
        persistence.__resetPersistenceForTests();
        mountPromptBox("");
        mountVideoStagesData({ clips: [clipRecord(5), clipRecord(5)] });
        saveSpy = jest.spyOn(persistence, "saveClips");
        committed = [];
        body = document.createElement("div");
        document.body.appendChild(body);
        render();
        track = createTimelinePlayhead({
            setSeconds: (seconds) => {
                committed.push(seconds);
                render();
            },
        });
        track.attach(body);
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    const rulerDown = (clientX: number): void => {
        body.querySelector<HTMLElement>(".vst-ruler")?.dispatchEvent(
            new MouseEvent("mousedown", { bubbles: true, clientX }),
        );
    };
    const move = (clientX: number): void => {
        document.dispatchEvent(new MouseEvent("mousemove", { clientX }));
    };
    const up = (clientX: number): void => {
        document.dispatchEvent(new MouseEvent("mouseup", { clientX }));
    };

    it("ruler mousedown sets the position and commits on release", () => {
        rulerDown(HEADER_W + 1 * PPS); // t = 1s
        up(HEADER_W + 1 * PPS);
        expect(committed).toEqual([1]);
    });

    it("drag scrubs live and clamps past the sequence end", () => {
        rulerDown(HEADER_W + 1 * PPS);
        // Way past the end: total is 10s.
        move(HEADER_W + 100 * PPS);
        const line = body.querySelector<HTMLElement>("[data-vst-playhead]");
        expect(line?.style.left).toBe(`${HEADER_W + 10 * PPS}px`);
        up(HEADER_W + 100 * PPS);
        expect(committed).toEqual([10]);
    });

    it("snaps the scrubbed position to the frame grid", () => {
        // t = 1.02s at 24fps snaps back to 1.0s.
        rulerDown(HEADER_W + 1.02 * PPS);
        up(HEADER_W + 1.02 * PPS);
        expect(committed[0]).toBeCloseTo(1.0, 5);
    });

    it("moves the under-head marker across the seam during scrub", () => {
        rulerDown(HEADER_W + 2 * PPS); // clip 0
        expect(
            body
                .querySelector('.vst-region[data-clip-idx="0"]')
                ?.classList.contains("vst-under-head"),
        ).toBe(true);
        move(HEADER_W + 5 * PPS); // exactly the seam -> clip 1
        expect(
            body
                .querySelector('.vst-region[data-clip-idx="1"]')
                ?.classList.contains("vst-under-head"),
        ).toBe(true);
        expect(
            body
                .querySelector('.vst-region[data-clip-idx="0"]')
                ?.classList.contains("vst-under-head"),
        ).toBe(false);
        up(HEADER_W + 5 * PPS);
    });

    it("updates the readout chip live during scrub", () => {
        rulerDown(HEADER_W + 2 * PPS);
        move(HEADER_W + 4 * PPS);
        expect(body.querySelector("[data-vst-readout-head]")?.textContent).toBe(
            "▸ 4.0s · f96",
        );
        up(HEADER_W + 4 * PPS);
    });

    it("never writes clip data while scrubbing (zero saveClips)", () => {
        rulerDown(HEADER_W + 2 * PPS);
        move(HEADER_W + 3 * PPS);
        move(HEADER_W + 6 * PPS);
        up(HEADER_W + 6 * PPS);
        expect(saveSpy).not.toHaveBeenCalled();
    });
});
