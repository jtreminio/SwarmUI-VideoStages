import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { storedClip } from "./__test_helpers__/clipFixtures";
import {
    firstSavedClips,
    mountPromptBox,
    mountTimelineBody,
    mountVideoStagesData,
    mouse,
    requireEl,
    stubRect,
    TIMELINE_PPS,
} from "./__test_helpers__/dom";
import { createGestureRouter, type GestureRouter } from "./gestureRouter";
import * as persistence from "./persistence/repository";
import {
    getSelection,
    resetSelectionForTests,
    setSelection,
} from "./selection";
import {
    createTimelinePromptTrack,
    type TimelinePromptTrack,
} from "./timelinePromptTrack";
import { spanGeometry } from "./trackDomUtils";
import type { Clip, PromptWindow } from "./types";

interface WindowFixture {
    prompt?: string;
    start: number;
    duration: number;
}
interface ClipFixture {
    duration: number;
    windows?: WindowFixture[];
}

const promptText = (clips: ClipFixture[]): string => {
    const tags: string[] = [];
    clips.forEach((clip, i) => {
        for (const w of clip.windows ?? []) {
            const end = w.start + w.duration;
            tags.push(`<videoclip[${i}]:${w.start}-${end}>${w.prompt ?? ""}`);
        }
    });
    return tags.join("\n");
};

const mountPrompt = (clips: ClipFixture[]): HTMLTextAreaElement => {
    mountVideoStagesData({
        clips: clips.map((clip) => storedClip({ duration: clip.duration })),
    });
    return mountPromptBox(promptText(clips));
};

/** The minor lane sits under the major band, so its rect's top is never its left. */
const MINOR_LANE_TOP = 40;

// Render a minimal prompt-track DOM (no full renderTimeline) with the data-* hooks the module reads.
const renderPromptTrack = (body: HTMLElement, clips: ClipFixture[]): void => {
    let cursor = 0;
    const parts: string[] = [];
    clips.forEach((clip, i) => {
        const startPx = cursor * TIMELINE_PPS;
        const widthPx = clip.duration * TIMELINE_PPS;
        parts.push(
            `<div class="vst-major-seg" data-vst-prompt="major" data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px"></div>`,
        );
        const segs = (clip.windows ?? [])
            .map((w, j) => {
                // Same projection the real segment renders through, so an
                // assertion on the rendered position measures production and
                // not this fixture.
                const { left, width } = spanGeometry(
                    w.start,
                    w.duration,
                    clip.duration,
                    { unit: "px", pxPerSecond: TIMELINE_PPS, minWidth: 2 },
                );
                return (
                    `<div class="vst-minor-seg" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${j}" style="left:${left}px;width:${width}px">` +
                    `<span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left"></span>` +
                    `<span class="vst-minor-text"></span>` +
                    `<span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right"></span>` +
                    `</div>`
                );
            })
            .join("");
        parts.push(
            `<div class="vst-minor-lane" data-vst-prompt-add data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px">${segs}</div>`,
        );
        cursor += clip.duration;
    });
    body.innerHTML =
        `<div class="vst-track-row vst-track-prompt"><div class="vst-track-cell vst-prompt-cell">` +
        parts.join("") +
        `</div></div>`;
    cursor = 0;
    clips.forEach((clip, i) => {
        const lane = body.querySelector<HTMLElement>(
            `.vst-minor-lane[data-clip-idx="${i}"]`,
        );
        if (lane) {
            stubRect(
                lane,
                cursor * TIMELINE_PPS,
                clip.duration * TIMELINE_PPS,
                MINOR_LANE_TOP,
            );
        }
        cursor += clip.duration;
    });
};

const savedWindows = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
    clipIdx = 0,
): PromptWindow[] => firstSavedClips<Clip[]>(spy)[clipIdx].promptWindows;

describe("createTimelinePromptTrack (DOM gestures)", () => {
    let track: TimelinePromptTrack | null = null;
    let router: GestureRouter | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        persistence.__resetPersistenceForTests();
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

    const setup = (clips: ClipFixture[]): HTMLElement => {
        mountPrompt(clips);
        const body = mountTimelineBody();
        renderPromptTrack(body, clips);
        track = createTimelinePromptTrack();
        router = createGestureRouter();
        router.attach(body);
        track.attach(body, router);
        return body;
    };

    it("clicking empty lane space adds a default-width minor window at the click point", () => {
        const body = setup([{ duration: 10 }]);
        const lane = requireEl(body, ".vst-minor-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 2 * TIMELINE_PPS));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const windows = savedWindows(saveSpy);
        expect(windows).toHaveLength(1);
        expect(windows[0].start).toBeCloseTo(2, 5);
        expect(windows[0].duration).toBeGreaterThan(0);
        expect(windows[0].prompt).toBe("");
    });

    it("selects the newly created window so the dock opens it ready to type", () => {
        const body = setup([{ duration: 10 }]);
        const lane = requireEl(body, ".vst-minor-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 2 * TIMELINE_PPS));

        // A brand-new window is the only one, so its index is 0.
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });
    });

    it("selects the correct index when the new window sorts among existing ones", () => {
        // Existing window at 6s; a new window created at 2s sorts to index 0,
        // pushing the existing one to index 1 — selection must track the NEW one.
        const body = setup([
            { duration: 10, windows: [{ start: 6, duration: 2 }] },
        ]);
        const lane = requireEl(body, ".vst-minor-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 2 * TIMELINE_PPS));

        const windows = savedWindows(saveSpy);
        expect(windows).toHaveLength(2);
        const newIdx = windows.findIndex((w) => Math.abs(w.start - 2) < 1e-3);
        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: newIdx,
        });
    });

    it("click-dragging on empty lane space adds a window sized to the drag", () => {
        const body = setup([{ duration: 10 }]);
        const lane = requireEl(body, ".vst-minor-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 4 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 4 * TIMELINE_PPS));

        const windows = savedWindows(saveSpy);
        expect(windows).toHaveLength(1);
        expect(windows[0].start).toBeCloseTo(1, 5);
        expect(windows[0].duration).toBeCloseTo(3, 5);
    });

    it("dragging a minor segment body moves it in time", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 2 }] },
        ]);
        const seg = requireEl(body, ".vst-minor-seg[data-window-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 2 * TIMELINE_PPS));

        const windows = savedWindows(saveSpy);
        expect(windows[0].start).toBeCloseTo(3, 5);
        expect(windows[0].duration).toBeCloseTo(2, 5);
    });

    it("a committed drag selects the dragged window, not any previous one", () => {
        // Regression: with W0 selected (its dock editor focused), dragging W1
        // used to leave W0 selected — the dock's focus-restore re-pointed the
        // selection back to W0's editor after the commit rebuild.
        const body = setup([
            {
                duration: 20,
                windows: [
                    { start: 1, duration: 2, prompt: "first" },
                    { start: 8, duration: 2, prompt: "second" },
                ],
            },
        ]);
        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        const seg = requireEl(body, ".vst-minor-seg[data-window-idx='1']");
        seg.dispatchEvent(mouse("mousedown", 400));
        document.dispatchEvent(mouse("mousemove", 400 + 2 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 400 + 2 * TIMELINE_PPS));

        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 1,
        });
    });

    it("a committed edge resize selects the resized window", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 2 }] },
        ]);
        const grip = requireEl(body, ".vst-minor-resize-r");
        grip.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mousemove", 200 + 1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 200 + 1 * TIMELINE_PPS));

        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });
    });

    it("clicking a minor segment (no drag) leaves its rendered position untouched", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 4, duration: 2 }] },
        ]);
        const seg = requireEl(body, ".vst-minor-seg[data-window-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mouseup", 200));
        seg.dispatchEvent(mouse("click", 200));

        // Where spanGeometry puts a 4s start at TIMELINE_PPS, not collapsed to 0.
        expect(seg.style.left).toBe(`${4 * TIMELINE_PPS}px`);
        expect(saveSpy).not.toHaveBeenCalled(); // a click opens the editor, it doesn't move/save
    });

    it("dragging a minor segment toward a neighbor stops before overlapping it", () => {
        const body = setup([
            {
                duration: 20,
                windows: [
                    { start: 2, duration: 4 }, // A: [2,6]
                    { start: 10, duration: 5 }, // B: [10,15]
                ],
            },
        ]);
        // Drag A far to the right, past B's left edge.
        const seg = requireEl(body, ".vst-minor-seg[data-window-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 8 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 8 * TIMELINE_PPS));

        const [a, b] = savedWindows(saveSpy).sort((x, y) => x.start - y.start);
        // A must not extend past B's start (10s); no overlap.
        expect(a.start + a.duration).toBeLessThanOrEqual(b.start + 1e-6);
        expect(a.duration).toBeCloseTo(4, 5);
    });

    it("resizing the right edge changes duration and keeps start", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 2 }] },
        ]);
        const grip = requireEl(body, ".vst-minor-resize-r");
        grip.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mousemove", 200 + 1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 200 + 1 * TIMELINE_PPS));

        const windows = savedWindows(saveSpy);
        expect(windows[0].start).toBeCloseTo(1, 5);
        expect(windows[0].duration).toBeCloseTo(3, 5);
    });

    it("resizing the left edge changes start and keeps the end fixed", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 2, duration: 2 }] },
        ]);
        const grip = requireEl(body, ".vst-minor-resize-l");
        grip.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mousemove", 200 - 1 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 200 - 1 * TIMELINE_PPS));

        const windows = savedWindows(saveSpy);
        expect(windows[0].start).toBeCloseTo(1, 5);
        expect(windows[0].duration).toBeCloseTo(3, 5); // end stays at 4s
    });

    it("shift+click removes the window", () => {
        const body = setup([
            {
                duration: 10,
                windows: [
                    { start: 1, duration: 1 },
                    { start: 4, duration: 1 },
                ],
            },
        ]);
        requireEl(body, ".vst-minor-seg[data-window-idx='0']").dispatchEvent(
            mouse("click", 50, { shiftKey: true }),
        );

        const windows = savedWindows(saveSpy);
        expect(windows).toHaveLength(1);
        expect(windows[0].start).toBeCloseTo(4, 5);
    });

    it("clicking a minor segment selects its relay window", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 2 }] },
        ]);
        const seg = requireEl(body, ".vst-minor-seg[data-window-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mouseup", 100));
        seg.dispatchEvent(mouse("click", 100));

        expect(getSelection()).toEqual({
            kind: "prompt-minor",
            clipIdx: 0,
            windowIdx: 0,
        });
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("clicking the MAJOR segment selects the clip's prompt", () => {
        const body = setup([{ duration: 10 }]);
        requireEl(body, ".vst-major-seg[data-clip-idx='0']").dispatchEvent(
            mouse("click", 100),
        );

        expect(getSelection()).toEqual({ kind: "prompt-major", clipIdx: 0 });
        expect(saveSpy).not.toHaveBeenCalled();
    });

    it("adding into a gap between two windows clamps to the free interval", () => {
        const body = setup([
            {
                duration: 10,
                windows: [
                    { start: 0, duration: 3 },
                    { start: 5, duration: 3 },
                ],
            },
        ]);
        const lane = requireEl(body, ".vst-minor-lane[data-clip-idx='0']");
        // Drag across the whole clip from inside the [3,5] gap; must stay within it.
        lane.dispatchEvent(mouse("mousedown", 4 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mousemove", 9 * TIMELINE_PPS));
        document.dispatchEvent(mouse("mouseup", 9 * TIMELINE_PPS));

        const added = savedWindows(saveSpy).find(
            (w) => w.start >= 3 - 1e-6 && w.start + w.duration <= 5 + 1e-6,
        );
        if (!added) {
            throw new Error("expected a window added within the [3,5] gap");
        }
        expect(added.start).toBeGreaterThanOrEqual(3 - 1e-6);
        expect(added.start + added.duration).toBeLessThanOrEqual(5 + 1e-6);
    });
});
