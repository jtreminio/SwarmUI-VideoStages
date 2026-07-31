import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { boundaryOverlapConstraints } from "./architectures/boundaryConstraints";
import { crossfadePlanForClips } from "./boundaryPlan";
import * as persistence from "./persistence";
import { getSelection, resetSelectionForTests } from "./selection";
import {
    createTimelineSelectionTracks,
    type TimelineSelectionTracks,
} from "./timelineSelectionTracks";
import { computeRegionLayout } from "./timelineView/layout";
import { renderBoundarySeams } from "./timelineView/regionRenderer";
import type { BoundaryOut, Clip } from "./types";

const PPS = 44;
const ltxBoundaryConstraints = (
    _clip: Clip,
    _index: number,
    mode: BoundaryOut,
) =>
    boundaryOverlapConstraints(
        testArchitectureCatalog().architectures[0].boundaryRules[mode],
    );

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

describe("crossfadePlanForClips", () => {
    it("reports no overlap when every boundary is a cut", () => {
        const plan = crossfadePlanForClips(
            [clipFor("cut"), clipFor("cut")],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan).toEqual({ overlaps: [0], fallback: false });
    });

    it("resolves a continue boundary to the requested overlap + 1", () => {
        // Default overlap 8 -> window 9 (8n+1), ample for 2s @ 24fps clips.
        const plan = crossfadePlanForClips(
            [clipFor("continue"), clipFor("cut")],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan.overlaps[0]).toBe(9);
        expect(plan.fallback).toBe(false);
    });

    it("clamps the shared crossfade window to 8 for ample clips", () => {
        // 2s @ 24fps -> ceil(48/8)*8+1 = 49 frames; budget 48 >> 8.
        const plan = crossfadePlanForClips(
            [clipFor("crossfade"), clipFor("cut")],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan.overlaps[0]).toBe(8);
    });

    it("falls back to a cut when a clip is too short for the overlap", () => {
        // duration 0 -> budget 0, unreachable with real 8-frame-aligned durations;
        // guard still mirrors backend math.
        const plan = crossfadePlanForClips(
            [clipFor("crossfade", 0), clipFor("cut", 2)],
            24,
            ltxBoundaryConstraints,
        );
        expect(plan.fallback).toBe(true);
        expect(plan.overlaps).toEqual([0]);
    });
});

describe("timeline boundary selection wiring", () => {
    let track: TimelineSelectionTracks | null = null;
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
        track = createTimelineSelectionTracks();
        track.attach(body);
        return body;
    };

    const clickSeam = (body: HTMLElement, leftClipIdx: number): void => {
        body.querySelector<HTMLElement>(
            `[data-vst-boundary-chip][data-left-clip-idx="${leftClipIdx}"]`,
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    };

    it("selects the boundary without mutating or saving on click", () => {
        const body = setup([{ duration: 5 }, { duration: 5 }]);
        clickSeam(body, 0);
        expect(getSelection()).toEqual({ kind: "boundary", leftClipIdx: 0 });
        expect(persistence.getClips()[0].boundaryOut).toBe("cut");
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("activates via keyboard (Enter)", () => {
        const body = setup([{ duration: 5 }, { duration: 5 }]);
        body.querySelector<HTMLElement>(
            '[data-vst-boundary-chip][data-left-clip-idx="0"]',
        )?.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "boundary", leftClipIdx: 0 });
        expect(saveSpy).not.toHaveBeenCalled();
    });
});
