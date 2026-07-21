import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { crossfadePlanForClips } from "./boundaryPlan";
import * as persistence from "./persistence";
import { getSelection, resetSelectionForTests } from "./selection";
import {
    createTimelineBoundaryTrack,
    nextBoundary,
    type TimelineBoundaryTrack,
} from "./timelineBoundaryTrack";
import { computeRegionLayout, renderBoundarySeams } from "./timelineView";
import type { BoundaryOut, Clip } from "./types";

const PPS = 44;

interface ClipFixture {
    duration: number;
    boundaryOut?: BoundaryOut;
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    boundaryOut: clip.boundaryOut ?? "cut",
    stages: [{}],
    refs: [],
    promptWindows: [],
});

const clipFor = (boundaryOut: BoundaryOut, duration = 2): Clip =>
    ({ duration, boundaryOut, stages: [], refs: [] }) as unknown as Clip;

describe("nextBoundary", () => {
    it("cycles cut → continue → crossfade → cut", () => {
        expect(nextBoundary("cut")).toBe("continue");
        expect(nextBoundary("continue")).toBe("crossfade");
        expect(nextBoundary("crossfade")).toBe("cut");
    });
});

describe("crossfadePlanForClips", () => {
    it("reports no overlap when every boundary is a cut", () => {
        const plan = crossfadePlanForClips(
            [clipFor("cut"), clipFor("cut")],
            24,
        );
        expect(plan).toEqual({ overlaps: [0], fallback: false });
    });

    it("gives a continue boundary a fixed 1-frame overlap", () => {
        const plan = crossfadePlanForClips(
            [clipFor("continue"), clipFor("cut")],
            24,
        );
        expect(plan.overlaps[0]).toBe(1);
        expect(plan.fallback).toBe(false);
    });

    it("clamps the shared crossfade window to 8 for ample clips", () => {
        // 2s @ 24fps -> ceil(48/8)*8+1 = 49 frames; budget 48 >> 8.
        const plan = crossfadePlanForClips(
            [clipFor("crossfade"), clipFor("cut")],
            24,
        );
        expect(plan.overlaps[0]).toBe(8);
    });

    it("falls back to a cut when a clip is too short for the overlap", () => {
        // duration 0 -> budget 0, unreachable with real 8-frame-aligned durations;
        // guard still mirrors backend math.
        const plan = crossfadePlanForClips(
            [clipFor("crossfade", 0), clipFor("cut", 2)],
            24,
        );
        expect(plan.fallback).toBe(true);
        expect(plan.overlaps).toEqual([0]);
    });
});

describe("createTimelineBoundaryTrack (cycle + select wiring)", () => {
    let track: TimelineBoundaryTrack | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        resetSelectionForTests();
        persistence.__resetPersistenceForTests();
        saveSpy = jest.spyOn(persistence, "saveClips");
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        jest.restoreAllMocks();
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    const setup = (fixtures: ClipFixture[]): HTMLElement => {
        mountPromptBox("");
        mountVideoStagesData({ clips: fixtures.map(clipRecord) });
        const body = document.createElement("div");
        body.id = "videostages-timeline-body";
        document.body.appendChild(body);
        const clips = persistence.getClips();
        const layouts = computeRegionLayout(clips, { pxPerSecond: PPS });
        body.innerHTML = renderBoundarySeams(clips, layouts);
        track = createTimelineBoundaryTrack();
        track.attach(body);
        return body;
    };

    const savedClips = (): Clip[] =>
        saveSpy.mock.calls[saveSpy.mock.calls.length - 1][0] as Clip[];

    const clickSeam = (body: HTMLElement, leftClipIdx: number): void => {
        body.querySelector<HTMLElement>(
            `[data-vst-boundary-cycle][data-left-clip-idx="${leftClipIdx}"]`,
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    };

    it("cycles the outgoing boundary and selects it on click", () => {
        const body = setup([{ duration: 5 }, { duration: 5 }]);
        clickSeam(body, 0);
        expect(getSelection()).toEqual({ kind: "boundary", leftClipIdx: 0 });
        expect(savedClips()[0].boundaryOut).toBe("continue");
    });

    it("cycles continue → crossfade on a second activation", () => {
        const body = setup([
            { duration: 5, boundaryOut: "continue" },
            { duration: 5 },
        ]);
        clickSeam(body, 0);
        expect(savedClips()[0].boundaryOut).toBe("crossfade");
    });

    it("activates via keyboard (Enter)", () => {
        const body = setup([{ duration: 5 }, { duration: 5 }]);
        body.querySelector<HTMLElement>(
            '[data-vst-boundary-cycle][data-left-clip-idx="0"]',
        )?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "boundary", leftClipIdx: 0 });
        expect(savedClips()[0].boundaryOut).toBe("continue");
    });
});
