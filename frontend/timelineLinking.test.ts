import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import {
    firstSavedClips,
    mountPromptBox,
    mountTimelineBody,
    mountVideoFps,
    mountVideoStagesData,
    mouse,
} from "./__test_helpers__/dom";
import { createGestureRouter, type GestureRouter } from "./gestureRouter";
import * as persistence from "./persistence/repository";
import { resetSelectionForTests } from "./selection";
import {
    createTimelineLinking,
    resolveSelectedIndex,
    type TimelineLinking,
} from "./timelineLinking";
import { DEFAULT_PX_PER_SECOND } from "./timelineView";
import { livePxPerSecond, parseIntAttr } from "./trackDomUtils";
import type { Clip } from "./types";

describe("resolveSelectedIndex", () => {
    it("keeps a valid in-range index", () => {
        expect(resolveSelectedIndex(1, 3)).toBe(1);
        expect(resolveSelectedIndex(0, 1)).toBe(0);
    });

    it("clears a null / out-of-range / removed selection", () => {
        expect(resolveSelectedIndex(null, 3)).toBeNull();
        expect(resolveSelectedIndex(3, 3)).toBeNull();
        expect(resolveSelectedIndex(-1, 3)).toBeNull();
        expect(resolveSelectedIndex(0, 0)).toBeNull();
    });

    it("rejects non-integers", () => {
        expect(resolveSelectedIndex(1.5, 3)).toBeNull();
    });
});

describe("parseIntAttr (data-clip-idx)", () => {
    it("parses a valid data-clip-idx", () => {
        const el = document.createElement("div");
        el.setAttribute("data-clip-idx", "2");
        expect(parseIntAttr(el, "data-clip-idx")).toBe(2);
    });

    it("returns null for missing/invalid/null", () => {
        expect(parseIntAttr(null, "data-clip-idx")).toBeNull();
        expect(
            parseIntAttr(document.createElement("div"), "data-clip-idx"),
        ).toBeNull();
        const bad = document.createElement("div");
        bad.setAttribute("data-clip-idx", "x");
        expect(parseIntAttr(bad, "data-clip-idx")).toBeNull();
    });
});

describe("livePxPerSecond", () => {
    it("reads the stamped px-per-second, else falls back to the default", () => {
        const body = document.createElement("div");
        expect(livePxPerSecond(body)).toBe(DEFAULT_PX_PER_SECOND);
        body.dataset.vstPps = "88";
        expect(livePxPerSecond(body)).toBe(88);
        body.dataset.vstPps = "0";
        expect(livePxPerSecond(body)).toBe(DEFAULT_PX_PER_SECOND);
    });
});

// The config rides in the hidden VideoStages Data param.
const clipsSection = (
    clips: Array<Record<string, unknown>>,
    fps?: number,
): HTMLTextAreaElement => {
    mountPromptBox("");
    if (fps !== undefined) {
        mountVideoFps(fps);
    }
    return mountVideoStagesData({ clips });
};

const durationClips = (durations: number[]): Array<Record<string, unknown>> =>
    durations.map(
        (duration): Record<string, unknown> => ({
            duration,
            stages: [{}],
            frameRefs: [],
        }),
    );

const renderRegions = (body: HTMLElement, count: number): void => {
    body.innerHTML = Array.from(
        { length: count },
        (_, i) =>
            `<div class="vst-region" data-clip-idx="${i}">` +
            `<span class="vst-region-name">Clip ${i}</span>` +
            `<button type="button" data-vst-region-action="skip"></button>` +
            `<div class="vst-region-resize"></div>` +
            `</div>`,
    ).join("");
};

const region = (body: HTMLElement, idx: number): HTMLElement => {
    const el = body.querySelector<HTMLElement>(
        `.vst-region[data-clip-idx="${idx}"]`,
    );
    if (!el) {
        throw new Error(`region ${idx} not found`);
    }
    return el;
};

// jsdom does no layout, so hand each region a fixed 100px-wide rect at index*100.
const stubRegionRects = (body: HTMLElement): void => {
    for (const el of body.querySelectorAll<HTMLElement>(
        ".vst-region[data-clip-idx]",
    )) {
        const idx = Number(el.getAttribute("data-clip-idx"));
        const left = idx * 100;
        el.getBoundingClientRect = (() =>
            ({
                left,
                width: 100,
                right: left + 100,
                top: 0,
                bottom: 182,
                height: 182,
                x: left,
                y: 0,
                toJSON: () => ({}),
            }) as DOMRect) as HTMLElement["getBoundingClientRect"];
    }
};

describe("createTimelineLinking selection + write gestures (DOM)", () => {
    let linking: TimelineLinking | null = null;
    let router: GestureRouter | null = null;

    beforeEach(() => {
        resetSelectionForTests();
        persistence.__resetPersistenceForTests();
    });

    afterEach(() => {
        linking?.dispose();
        router?.dispose();
        router = null;
        linking = null;
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    it("clicking a region selects it (single-selection, no write)", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = mountTimelineBody();
        renderRegions(body, 3);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);
        linking.reapplySelection(body, 3);

        region(body, 1).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );

        expect(region(body, 1).classList.contains("vst-region-selected")).toBe(
            true,
        );
        expect(region(body, 0).classList.contains("vst-region-selected")).toBe(
            false,
        );
        expect(saveSpy).not.toHaveBeenCalled();
        expect(linking.getSelectedIndex()).toBe(1);
    });

    it("re-applies the selection highlight after a re-render and clears a removed selection", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = mountTimelineBody();
        renderRegions(body, 3);

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);
        linking.reapplySelection(body, 3);
        region(body, 2).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );

        renderRegions(body, 3);
        expect(region(body, 2).classList.contains("vst-region-selected")).toBe(
            false,
        );
        linking.reapplySelection(body, 3);
        expect(region(body, 2).classList.contains("vst-region-selected")).toBe(
            true,
        );

        // Clip 2 removed => only 2 clips remain, selection cleared.
        renderRegions(body, 2);
        linking.reapplySelection(body, 2);
        expect(body.querySelectorAll(".vst-region-selected")).toHaveLength(0);
    });

    it("the skip button commits one document command without selecting the region", () => {
        clipsSection(durationClips([1, 2]));
        const body = mountTimelineBody();
        renderRegions(body, 2);
        const saveSpy = jest.spyOn(persistence, "saveClips");
        const beforeRevision = persistence.getTimelineStore().revision();

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);

        region(body, 1)
            .querySelector<HTMLButtonElement>("[data-vst-region-action='skip']")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).not.toHaveBeenCalled();
        expect(persistence.getClips()[1].skipped).toBe(true);
        expect(persistence.getTimelineStore().revision()).toBe(
            beforeRevision + 1,
        );
        expect(region(body, 1).classList.contains("vst-region-selected")).toBe(
            false,
        );
    });

    it("does not skip or shift-delete the first clip", () => {
        clipsSection(durationClips([1, 2]));
        const body = mountTimelineBody();
        renderRegions(body, 2);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);

        region(body, 0)
            .querySelector<HTMLButtonElement>("[data-vst-region-action='skip']")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        region(body, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: true }),
        );

        expect(saveSpy).not.toHaveBeenCalled();
        expect(persistence.getClips()).toHaveLength(2);
        expect(persistence.getClips()[0].skipped).toBe(false);
    });

    it("shift+click removes the clip via saveClips", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = mountTimelineBody();
        renderRegions(body, 3);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);

        region(body, 1).dispatchEvent(
            new MouseEvent("click", { bubbles: true, shiftKey: true }),
        );

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(
            firstSavedClips<Clip[]>(saveSpy).map((clip) => clip.duration),
        ).toEqual([1, 3]);
    });

    it("dragging region 0 past region 1 persists the reordered clips and keeps the moved clip selected", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = mountTimelineBody();
        renderRegions(body, 3);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);
        linking.reapplySelection(body, 3);

        // Grab region 0 (centre x=50), drag right past region 1's midpoint (x=220 → gap index 2), release.
        region(body, 0).dispatchEvent(mouse("mousedown", 50, { clientY: 90 }));
        document.dispatchEvent(mouse("mousemove", 220, { clientY: 90 }));
        expect(body.querySelectorAll(".vst-drop-indicator")).toHaveLength(1);
        document.dispatchEvent(mouse("mouseup", 220, { clientY: 90 }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(
            firstSavedClips<Clip[]>(saveSpy).map((clip) => clip.duration),
        ).toEqual([2, 1, 3]);
        expect(body.querySelectorAll(".vst-drop-indicator")).toHaveLength(0);
        expect(linking?.getSelectedIndex()).toBe(1);
    });

    it("a plain click (no movement) selects the region and does NOT write", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = mountTimelineBody();
        renderRegions(body, 3);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);
        linking.reapplySelection(body, 3);

        region(body, 0).dispatchEvent(mouse("mousedown", 50, { clientY: 90 }));
        document.dispatchEvent(mouse("mouseup", 50, { clientY: 90 }));
        region(body, 0).dispatchEvent(
            new MouseEvent("click", { bubbles: true }),
        );

        expect(region(body, 0).classList.contains("vst-region-selected")).toBe(
            true,
        );
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("edge-dragging a region's right border changes only that clip's duration via one saveClips", () => {
        clipsSection(durationClips([2, 2]));
        const body = mountTimelineBody();
        renderRegions(body, 2);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);

        // Grab region 0's right grip, drag from x=100 to x=176 (176px / 44px/s = 4s at 24fps).
        region(body, 0)
            .querySelector<HTMLElement>(".vst-region-resize")
            ?.dispatchEvent(mouse("mousedown", 100, { clientY: 90 }));
        document.dispatchEvent(mouse("mousemove", 176, { clientY: 90 }));
        document.dispatchEvent(mouse("mouseup", 176, { clientY: 90 }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = firstSavedClips<Clip[]>(saveSpy);
        expect(clips[0].duration).toBe(4);
        expect(clips[1].duration).toBe(2);
    });

    it("restores the clip's allocated half-join when resizing its unique region", () => {
        clipsSection(durationClips([3, 3]));
        const body = mountTimelineBody();
        renderRegions(body, 2);
        stubRegionRects(body);
        region(body, 0).dataset.vstJoinTrimSeconds = "0.5";
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);

        region(body, 0)
            .querySelector<HTMLElement>(".vst-region-resize")
            ?.dispatchEvent(mouse("mousedown", 100, { clientY: 90 }));
        document.dispatchEvent(mouse("mousemove", 176, { clientY: 90 }));
        document.dispatchEvent(mouse("mouseup", 176, { clientY: 90 }));

        expect(firstSavedClips<Clip[]>(saveSpy)[0].duration).toBe(4.5);
    });

    it("preserves a dormant reference when resizing an unresolved model", () => {
        clipsSection(
            [
                {
                    duration: 5,
                    stages: [{}],
                    frameRefs: [{ source: "Base", frame: 999 }],
                },
            ],
            16,
        );
        const body = mountTimelineBody();
        renderRegions(body, 1);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        router = createGestureRouter();
        router.attach(body);
        linking.attach(body, router);

        region(body, 0)
            .querySelector<HTMLElement>(".vst-region-resize")
            ?.dispatchEvent(mouse("mousedown", 100, { clientY: 90 }));
        document.dispatchEvent(mouse("mousemove", 56, { clientY: 90 }));
        document.dispatchEvent(mouse("mouseup", 56, { clientY: 90 }));

        const clips = firstSavedClips<Clip[]>(saveSpy);
        expect(persistence.getState().fps).toBe(16);
        expect(clips[0].duration).toBe(1.3);
        expect(clips[0].frameRefs[0].frame).toBe(999);
    });
});
