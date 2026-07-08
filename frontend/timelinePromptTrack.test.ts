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
    createTimelinePromptTrack,
    type TimelinePromptTrack,
} from "./timelinePromptTrack";
import type { Clip, PromptWindow } from "./types";

const PPS = 44;

interface WindowFixture {
    prompt?: string;
    start: number;
    duration: number;
    skipped?: boolean;
}
interface ClipFixture {
    duration: number;
    windows?: WindowFixture[];
}

const clipRecord = (clip: ClipFixture): Record<string, unknown> => ({
    duration: clip.duration,
    stages: [{}],
    refs: [],
    promptWindows: (clip.windows ?? []).map((w) => ({
        prompt: w.prompt ?? "",
        start: w.start,
        duration: w.duration,
        skipped: w.skipped ?? false,
    })),
});

const mountPrompt = (clips: ClipFixture[]): HTMLTextAreaElement => {
    const json = JSON.stringify({ clips: clips.map(clipRecord) });
    const input = document.createElement("textarea");
    input.id = "input_prompt";
    input.value = `<videostages>${json}`;
    document.body.appendChild(input);
    return input;
};

const makeBody = (): HTMLElement => {
    const body = document.createElement("div");
    body.id = "videostages-timeline-body";
    body.dataset.vstPps = String(PPS);
    document.body.appendChild(body);
    return body;
};

// Render a minimal prompt-track DOM (no full renderTimeline) with the data-* hooks the module reads.
const renderPromptTrack = (body: HTMLElement, clips: ClipFixture[]): void => {
    let cursor = 0;
    const parts: string[] = [];
    clips.forEach((clip, i) => {
        const startPx = cursor * PPS;
        const widthPx = clip.duration * PPS;
        parts.push(
            `<div class="vst-major-seg" data-vst-prompt="major" data-clip-idx="${i}" style="left:${startPx}px;width:${widthPx}px"></div>`,
        );
        const segs = (clip.windows ?? [])
            .map(
                (w, j) =>
                    `<div class="vst-minor-seg" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${j}" style="left:${w.start * PPS}px;width:${w.duration * PPS}px">` +
                    `<span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left"></span>` +
                    `<span class="vst-minor-text"></span>` +
                    `<button type="button" data-vst-minor-action="skip"></button>` +
                    `<button type="button" data-vst-minor-action="delete"></button>` +
                    `<span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right"></span>` +
                    `</div>`,
            )
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
    // jsdom does no layout — stub each lane's rect from its clip offset.
    cursor = 0;
    clips.forEach((clip, i) => {
        const left = cursor * PPS;
        const lane = body.querySelector<HTMLElement>(
            `.vst-minor-lane[data-clip-idx="${i}"]`,
        );
        if (lane) {
            lane.getBoundingClientRect = (() =>
                ({
                    left,
                    width: clip.duration * PPS,
                    right: left + clip.duration * PPS,
                    top: 40,
                    bottom: 72,
                    height: 32,
                    x: left,
                    y: 40,
                    toJSON: () => ({}),
                }) as DOMRect) as HTMLElement["getBoundingClientRect"];
        }
        cursor += clip.duration;
    });
};

const el = (body: HTMLElement, selector: string): HTMLElement => {
    const found = body.querySelector<HTMLElement>(selector);
    if (!found) {
        throw new Error(`not found: ${selector}`);
    }
    return found;
};

const requireEditor = (): HTMLTextAreaElement => {
    const editor =
        document.querySelector<HTMLTextAreaElement>(".vst-prompt-editor");
    if (!editor) {
        throw new Error("prompt editor not open");
    }
    return editor;
};

const commitEditor = (value: string): void => {
    const editor = requireEditor();
    editor.value = value;
    editor.dispatchEvent(
        new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
    );
};

const mouse = (type: string, clientX: number): MouseEvent =>
    new MouseEvent(type, {
        bubbles: true,
        clientX,
        clientY: 56,
        button: 0,
    });

const savedClips = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
): Clip[] => spy.mock.calls[0][0] as Clip[];

const savedWindows = (
    spy: jest.SpiedFunction<typeof persistence.saveClips>,
    clipIdx = 0,
): PromptWindow[] => savedClips(spy)[clipIdx].promptWindows;

describe("createTimelinePromptTrack (DOM gestures)", () => {
    let track: TimelinePromptTrack | null = null;
    let saveSpy: jest.SpiedFunction<typeof persistence.saveClips>;

    beforeEach(() => {
        saveSpy = jest
            .spyOn(persistence, "saveClips")
            .mockImplementation(() => {});
    });

    afterEach(() => {
        track?.dispose();
        track = null;
        jest.restoreAllMocks();
        document.body.innerHTML = "";
    });

    const setup = (clips: ClipFixture[]): HTMLElement => {
        mountPrompt(clips);
        const body = makeBody();
        renderPromptTrack(body, clips);
        track = createTimelinePromptTrack();
        track.attach(body);
        return body;
    };

    it("clicking empty lane space adds a default-width minor window at the click point", () => {
        const body = setup([{ duration: 10 }]);
        const lane = el(body, ".vst-minor-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 2 * PPS)); // t = 2s
        document.dispatchEvent(mouse("mouseup", 2 * PPS));

        expect(saveSpy).toHaveBeenCalledTimes(1);
        const windows = savedWindows(saveSpy);
        expect(windows).toHaveLength(1);
        expect(windows[0].start).toBeCloseTo(2, 5);
        expect(windows[0].duration).toBeGreaterThan(0);
        expect(windows[0].prompt).toBe("");
    });

    it("click-dragging on empty lane space adds a window sized to the drag", () => {
        const body = setup([{ duration: 10 }]);
        const lane = el(body, ".vst-minor-lane[data-clip-idx='0']");
        lane.dispatchEvent(mouse("mousedown", 1 * PPS)); // start 1s
        document.dispatchEvent(mouse("mousemove", 4 * PPS)); // drag to 4s
        document.dispatchEvent(mouse("mouseup", 4 * PPS));

        const windows = savedWindows(saveSpy);
        expect(windows).toHaveLength(1);
        expect(windows[0].start).toBeCloseTo(1, 5);
        expect(windows[0].duration).toBeCloseTo(3, 5);
    });

    it("dragging a minor segment body moves it in time", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 2 }] },
        ]);
        const seg = el(body, ".vst-minor-seg[data-window-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 2 * PPS)); // +2s
        document.dispatchEvent(mouse("mouseup", 100 + 2 * PPS));

        const windows = savedWindows(saveSpy);
        expect(windows[0].start).toBeCloseTo(3, 5);
        expect(windows[0].duration).toBeCloseTo(2, 5);
    });

    it("clicking a minor segment (no drag) leaves its rendered position untouched", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 4, duration: 2 }] },
        ]);
        const seg = el(body, ".vst-minor-seg[data-window-idx='0']");
        const before = seg.style.left; // rendered inline position
        seg.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mouseup", 200)); // no movement
        seg.dispatchEvent(mouse("click", 200));

        // The segment must not have collapsed to left:0 — its inline left is preserved.
        expect(seg.style.left).toBe(before);
        expect(seg.style.left).not.toBe("");
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
        const seg = el(body, ".vst-minor-seg[data-window-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mousemove", 100 + 8 * PPS));
        document.dispatchEvent(mouse("mouseup", 100 + 8 * PPS));

        const [a, b] = savedWindows(saveSpy).sort((x, y) => x.start - y.start);
        // A must not extend past B's start (10s); no overlap.
        expect(a.start + a.duration).toBeLessThanOrEqual(b.start + 1e-6);
        expect(a.duration).toBeCloseTo(4, 5); // duration preserved
    });

    it("resizing the right edge changes duration and keeps start", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 2 }] },
        ]);
        const grip = el(body, ".vst-minor-resize-r");
        grip.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mousemove", 200 + 1 * PPS)); // +1s
        document.dispatchEvent(mouse("mouseup", 200 + 1 * PPS));

        const windows = savedWindows(saveSpy);
        expect(windows[0].start).toBeCloseTo(1, 5);
        expect(windows[0].duration).toBeCloseTo(3, 5);
    });

    it("resizing the left edge changes start and keeps the end fixed", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 2, duration: 2 }] },
        ]);
        const grip = el(body, ".vst-minor-resize-l");
        grip.dispatchEvent(mouse("mousedown", 200));
        document.dispatchEvent(mouse("mousemove", 200 - 1 * PPS)); // -1s
        document.dispatchEvent(mouse("mouseup", 200 - 1 * PPS));

        const windows = savedWindows(saveSpy);
        expect(windows[0].start).toBeCloseTo(1, 5);
        expect(windows[0].duration).toBeCloseTo(3, 5); // end stays at 4s
    });

    it("the delete button removes the window", () => {
        const body = setup([
            {
                duration: 10,
                windows: [
                    { start: 1, duration: 1 },
                    { start: 4, duration: 1 },
                ],
            },
        ]);
        el(
            body,
            ".vst-minor-seg[data-window-idx='0'] [data-vst-minor-action='delete']",
        ).dispatchEvent(mouse("click", 50));

        const windows = savedWindows(saveSpy);
        expect(windows).toHaveLength(1);
        expect(windows[0].start).toBeCloseTo(4, 5);
    });

    it("the skip button toggles the window's skipped flag", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 1 }] },
        ]);
        el(
            body,
            ".vst-minor-seg[data-window-idx='0'] [data-vst-minor-action='skip']",
        ).dispatchEvent(mouse("click", 50));

        expect(savedWindows(saveSpy)[0].skipped).toBe(true);
    });

    it("clicking a minor segment opens an editor that commits the prompt on Enter", () => {
        const body = setup([
            { duration: 10, windows: [{ start: 1, duration: 2 }] },
        ]);
        const seg = el(body, ".vst-minor-seg[data-window-idx='0']");
        seg.dispatchEvent(mouse("mousedown", 100));
        document.dispatchEvent(mouse("mouseup", 100)); // no movement
        seg.dispatchEvent(mouse("click", 100));

        commitEditor("a red car");

        expect(savedWindows(saveSpy)[0].prompt).toBe("a red car");
        expect(document.querySelector(".vst-prompt-editor")).toBeNull();
    });

    it("clicking the MAJOR segment edits the clip's own prompt", () => {
        const body = setup([{ duration: 10 }]);
        el(body, ".vst-major-seg[data-clip-idx='0']").dispatchEvent(
            mouse("click", 100),
        );

        commitEditor("a wide landscape");

        expect(savedClips(saveSpy)[0].prompt).toBe("a wide landscape");
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
        const lane = el(body, ".vst-minor-lane[data-clip-idx='0']");
        // Drag across the whole clip from inside the [3,5] gap; must stay within it.
        lane.dispatchEvent(mouse("mousedown", 4 * PPS));
        document.dispatchEvent(mouse("mousemove", 9 * PPS));
        document.dispatchEvent(mouse("mouseup", 9 * PPS));

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
