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
    createTimelineReferencesTrack,
    type TimelineReferencesTrack,
} from "./timelineReferencesTrack";
import {
    computeRegionLayout,
    renderReferencesTrackRow,
    renderTimeline,
} from "./timelineView";
import type { Clip } from "./types";
import { getSelection, resetSelectionForTests, setSelection } from "./uiState";

const PPS = 44;

interface RefFixture {
    source?: string;
    frame?: number;
    fromEnd?: boolean;
}

interface ClipFixture {
    duration: number;
    refs?: RefFixture[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    audioSource: "Native",
    stages: [{}],
    refs: (clip.refs ?? []).map((ref) => ({
        source: ref.source ?? "Refiner",
        frame: ref.frame ?? 1,
        fromEnd: ref.fromEnd ?? false,
    })),
    promptWindows: [],
});

const mountPrompt = (clips: ClipFixture[]): void => {
    mountPromptBox("");
    mountVideoStagesData({ clips: clips.map(clipRecord) });
};

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
};

const renderRefs = (body: HTMLElement, clips: Clip[]): void => {
    const layouts = computeRegionLayout(clips, { pxPerSecond: PPS });
    body.innerHTML = renderReferencesTrackRow(clips, layouts, 24, "seconds");
};

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[spy.mock.calls.length - 1][0] as Clip[];

describe("createTimelineReferencesTrack (selection + gestures)", () => {
    let track: TimelineReferencesTrack | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        resetSelectionForTests();
        saveSpy = jest
            .spyOn(persistence, "saveClips")
            .mockImplementation(() => {});
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        jest.restoreAllMocks();
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    const setup = (fixtures: ClipFixture[]): HTMLElement => {
        mountPrompt(fixtures);
        const body = makeBody();
        renderRefs(body, persistence.getClips());
        track = createTimelineReferencesTrack();
        track.attach(body);
        return body;
    };

    const markEl = (
        body: HTMLElement,
        clipIdx: number,
        refIdx: number,
    ): HTMLElement => {
        const el = body.querySelector<HTMLElement>(
            `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`,
        );
        if (!el) {
            throw new Error(
                `ref thumb not found: clip ${clipIdx} ref ${refIdx}`,
            );
        }
        return el;
    };

    const stubLaneRect = (
        body: HTMLElement,
        clipIdx: number,
        left: number,
        width: number,
    ): void => {
        const lane = body.querySelector<HTMLElement>(
            `.vst-refs-lane[data-clip-idx="${clipIdx}"]`,
        );
        if (!lane) {
            throw new Error(`ref lane not found: clip ${clipIdx}`);
        }
        lane.getBoundingClientRect = (() =>
            ({
                left,
                width,
                right: left + width,
                top: 0,
                bottom: 40,
                height: 40,
                x: left,
                y: 0,
                toJSON: () => ({}),
            }) as DOMRect) as HTMLElement["getBoundingClientRect"];
    };

    const dragThumb = (
        thumb: HTMLElement,
        fromX: number,
        toX: number,
    ): void => {
        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: fromX,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: toX }),
        );
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: toX }),
        );
    };

    it("selects the reference when its thumb is clicked", () => {
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        markEl(body, 0, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("selects the reference via keyboard activation", () => {
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        markEl(body, 0, 0).dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
        );
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
    });

    it("deletes the reference (and its stage ref-strengths) via shift+click", () => {
        const body = setup([
            { duration: 5, refs: [{ source: "Refiner" }, { source: "Base" }] },
        ]);
        markEl(body, 0, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: true }),
        );

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = savedClips(saveSpy);
        expect(clips[0].refs).toHaveLength(1);
        expect(clips[0].refs[0].source).toBe("Base");
        expect(clips[0].stages[0].refStrengths).toHaveLength(1);
        expect(getSelection().kind).toBe("none");
    });

    it("adds a reference (padding every stage's ref-strengths) when the empty lane is clicked", () => {
        const body = setup([{ duration: 5, refs: [] }]);
        body.querySelector<HTMLElement>(
            '.vst-refs-lane[data-clip-idx="0"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = savedClips(saveSpy);
        expect(clips[0].refs).toHaveLength(1);
        expect(clips[0].stages[0].refStrengths).toHaveLength(1);
        // The new ref opens in the dock immediately.
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
    });

    it("adding a ref selects the NEW ref even while another ref is selected", () => {
        const body = setup([{ duration: 5, refs: [{ source: "Refiner" }] }]);
        setSelection({ kind: "ref", clipIdx: 0, refIdx: 0 });
        body.querySelector<HTMLElement>(
            '.vst-refs-lane[data-clip-idx="0"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(savedClips(saveSpy)[0].refs).toHaveLength(2);
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 1 });
    });

    it("retimes a ref by dragging its thumbnail, suppressing the trailing click", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        dragThumb(thumb, 0, 60);
        // A completed drag swallows the trailing click → no selection, no add.
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(getSelection().kind).toBe("none");
        expect(saveSpy).toHaveBeenCalledTimes(1);
        // Half-way across a 5s clip @24fps → frame 60.
        expect(savedClips(saveSpy)[0].refs[0].frame).toBe(60);
    });

    it("treats a sub-threshold press as a select, not a drag", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 2 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        dragThumb(thumb, 40, 42); // 2px < the 5px drag threshold
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(getSelection()).toEqual({ kind: "ref", clipIdx: 0, refIdx: 0 });
    });

    it("cancels an in-flight drag on Escape without saving", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 10 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);

        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: 0,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );
        document.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
        );
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );
        thumb.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(getSelection().kind).toBe("none");
    });

    it("live-updates the label and holds position on a committed drag (no flash-back)", () => {
        const body = setup([{ duration: 5, refs: [{ frame: 1 }] }]);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);
        const originalLeft = thumb.style.left;

        dragThumb(thumb, 0, 60);

        expect(thumb.querySelector(".vst-refs-ph")?.textContent).toBe("R 60");
        expect(thumb.style.left).toBe("50%");
        expect(thumb.style.left).not.toBe(originalLeft);
        expect(savedClips(saveSpy)[0].refs[0].frame).toBe(60);
    });

    it("drags the on-clip arrow together with the thumbnail", () => {
        mountPrompt([{ duration: 5, refs: [{ frame: 1 }] }]);
        const body = makeBody();
        renderTimeline(body, persistence.getClips(), { pxPerSecond: PPS });
        track = createTimelineReferencesTrack();
        track.attach(body);
        stubLaneRect(body, 0, 0, 120);
        const thumb = markEl(body, 0, 0);
        const arrow = body.querySelector<HTMLElement>(
            '.vst-key[data-ref-idx="0"]',
        );

        thumb.dispatchEvent(
            new MouseEvent("mousedown", {
                bubbles: true,
                button: 0,
                clientX: 0,
            }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );

        expect(arrow?.style.left).toBe("50%");
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );
    });
});
