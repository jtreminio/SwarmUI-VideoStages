import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import * as persistence from "./persistence";
import {
    createTimelineLinking,
    livePxPerSecond,
    parseClipIdx,
    parseRefIdx,
    resolveSelectedIndex,
    type TimelineLinking,
} from "./timelineLinking";
import { DEFAULT_PX_PER_SECOND } from "./timelineView";
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

describe("parseClipIdx", () => {
    it("parses a valid data-clip-idx", () => {
        const el = document.createElement("div");
        el.setAttribute("data-clip-idx", "2");
        expect(parseClipIdx(el)).toBe(2);
    });

    it("returns null for missing/invalid/null", () => {
        expect(parseClipIdx(null)).toBeNull();
        expect(parseClipIdx(document.createElement("div"))).toBeNull();
        const bad = document.createElement("div");
        bad.setAttribute("data-clip-idx", "x");
        expect(parseClipIdx(bad)).toBeNull();
    });
});

describe("parseRefIdx", () => {
    it("parses a valid data-ref-idx", () => {
        const el = document.createElement("div");
        el.setAttribute("data-ref-idx", "3");
        expect(parseRefIdx(el)).toBe(3);
    });

    it("returns null for missing/invalid/null", () => {
        expect(parseRefIdx(null)).toBeNull();
        expect(parseRefIdx(document.createElement("div"))).toBeNull();
        const bad = document.createElement("div");
        bad.setAttribute("data-ref-idx", "nope");
        expect(parseRefIdx(bad)).toBeNull();
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

// The config rides in the <videostages> section of the positive prompt.
const clipsSection = (
    clips: Array<Record<string, unknown>>,
): HTMLTextAreaElement => {
    const json = JSON.stringify({ clips });
    const input = document.createElement("textarea");
    input.id = "input_prompt";
    input.value = `<videostages>${json}`;
    document.body.appendChild(input);
    return input;
};

const durationClips = (durations: number[]): Array<Record<string, unknown>> =>
    durations.map(
        (duration): Record<string, unknown> => ({
            duration,
            stages: [{}],
            refs: [],
        }),
    );

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = "44";
    document.body.appendChild(body);
    return body;
};

const renderRegions = (body: HTMLElement, count: number): void => {
    body.innerHTML = Array.from(
        { length: count },
        (_, i) =>
            `<div class="vst-region" data-clip-idx="${i}">` +
            `<span class="vst-region-name">Clip ${i}</span>` +
            `<button type="button" data-vst-region-action="skip"></button>` +
            `<button type="button" data-vst-region-action="delete"></button>` +
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

const mouse = (
    type: string,
    clientX: number,
    opts: { shiftKey?: boolean } = {},
): MouseEvent =>
    new MouseEvent(type, {
        bubbles: true,
        clientX,
        clientY: 90,
        button: 0,
        shiftKey: opts.shiftKey ?? false,
    });

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[0][0] as Clip[];

describe("createTimelineLinking selection + write gestures (DOM)", () => {
    let linking: TimelineLinking | null = null;

    beforeEach(() => {
        persistence.__resetPersistenceForTests();
    });

    afterEach(() => {
        linking?.dispose();
        linking = null;
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    it("clicking a region selects it (single-selection, no write)", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = makeBody();
        renderRegions(body, 3);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        linking.attach(body);
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
        const body = makeBody();
        renderRegions(body, 3);

        linking = createTimelineLinking();
        linking.attach(body);
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

    it("the skip button toggles clip.skipped via saveClips without selecting the region", () => {
        clipsSection(durationClips([1, 2]));
        const body = makeBody();
        renderRegions(body, 2);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        linking.attach(body);

        region(body, 1)
            .querySelector<HTMLButtonElement>("[data-vst-region-action='skip']")
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[1].skipped).toBe(true);
        expect(region(body, 1).classList.contains("vst-region-selected")).toBe(
            false,
        );
    });

    it("the delete button removes the clip via saveClips", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = makeBody();
        renderRegions(body, 3);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        linking.attach(body);

        region(body, 1)
            .querySelector<HTMLButtonElement>(
                "[data-vst-region-action='delete']",
            )
            ?.dispatchEvent(new MouseEvent("click", { bubbles: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy).map((clip) => clip.duration)).toEqual([
            1, 3,
        ]);
    });

    it("dragging region 0 past region 1 persists the reordered clips and keeps the moved clip selected", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = makeBody();
        renderRegions(body, 3);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        linking.attach(body);
        linking.reapplySelection(body, 3);

        // Grab region 0 (centre x=50), drag right past region 1's midpoint (x=220 → gap index 2), release.
        region(body, 0).dispatchEvent(mouse("mousedown", 50));
        document.dispatchEvent(mouse("mousemove", 220));
        expect(body.querySelectorAll(".vst-drop-indicator")).toHaveLength(1);
        document.dispatchEvent(mouse("mouseup", 220));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy).map((clip) => clip.duration)).toEqual([
            2, 1, 3,
        ]);
        expect(body.querySelectorAll(".vst-drop-indicator")).toHaveLength(0);
        // The moved clip is now at index 1.
        expect(linking?.getSelectedIndex()).toBe(1);
    });

    it("a plain click (no movement) selects the region and does NOT write", () => {
        clipsSection(durationClips([1, 2, 3]));
        const body = makeBody();
        renderRegions(body, 3);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        linking.attach(body);
        linking.reapplySelection(body, 3);

        region(body, 0).dispatchEvent(mouse("mousedown", 50));
        document.dispatchEvent(mouse("mouseup", 50));
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
        const body = makeBody();
        renderRegions(body, 2);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        linking.attach(body);

        // Grab region 0's right grip, drag from x=100 to x=176 (176px / 44px/s = 4s at 24fps).
        region(body, 0)
            .querySelector<HTMLElement>(".vst-region-resize")
            ?.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 176));
        document.dispatchEvent(mouse("mouseup", 176));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const clips = savedClips(saveSpy);
        expect(clips[0].duration).toBe(4);
        expect(clips[1].duration).toBe(2);
    });
});

describe("createTimelineLinking keyframe editing (DOM)", () => {
    let linking: TimelineLinking | null = null;

    const renderPipRegion = (body: HTMLElement): void => {
        body.innerHTML =
            `<div class="vst-region" data-clip-idx="0">` +
            `<div class="vst-keys">` +
            `<span class="vst-key" data-clip-idx="0" data-ref-idx="0" role="button" tabindex="0" style="left:50%">` +
            `<span class="vst-key-dot"></span>` +
            `</span>` +
            `</div>` +
            `</div>`;
    };

    beforeEach(() => {
        persistence.__resetPersistenceForTests();
    });

    afterEach(() => {
        linking?.dispose();
        linking = null;
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    it("shift-clicking a pip toggles its ref.fromEnd via saveClips", () => {
        clipsSection([
            {
                duration: 2,
                stages: [{ refStrengths: [0.8] }],
                refs: [{ frame: 10, fromEnd: false }],
            },
        ]);
        const body = makeBody();
        renderPipRegion(body);
        stubRegionRects(body);
        const saveSpy = jest.spyOn(persistence, "saveClips");

        linking = createTimelineLinking();
        linking.attach(body);

        const dot = body.querySelector<HTMLElement>(".vst-key-dot");
        dot?.dispatchEvent(mouse("mousedown", 50, { shiftKey: true }));
        document.dispatchEvent(mouse("mouseup", 50, { shiftKey: true }));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        expect(savedClips(saveSpy)[0].refs[0].fromEnd).toBe(true);
    });
});
