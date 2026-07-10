import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { __resetPersistenceForTests } from "./persistence";
import { clearUiStateForTests } from "./uiState";
import {
    type VideoStagesTimeline,
    videoStagesTimeline,
} from "./videoStagesTimeline";

const TIMELINE_BODY_ID = "videostages-timeline-body";
// A little over the controller's 200ms poll interval, so one tick is guaranteed to fire.
const POLL_ADVANCE_MS = 250;

const setupBottomBar = (): void => {
    const nav = document.createElement("ul");
    nav.id = "bottombartabcollection";
    document.body.appendChild(nav);

    const content = document.createElement("div");
    content.id = "t2i_bottom_bar_content";
    document.body.appendChild(content);
};

const makeClipsJson = (count: number, duration = 2): string =>
    JSON.stringify({
        clips: Array.from({ length: count }, () => ({
            duration,
            stages: [{}] as unknown[],
            refs: [] as unknown[],
        })),
    });

// The structured config now rides in the hidden Data param (`#input_videostages`),
// NOT in the positive prompt. The timeline binds change/input listeners to
// `#input_prompt` and additionally polls the combined state token.
let dataInput: HTMLTextAreaElement;
let promptInput: HTMLTextAreaElement;

const mountState = (json: string): void => {
    dataInput = mountVideoStagesData(json);
    promptInput = mountPromptBox("");
};
/** Change the clip structure carried in the Data param. */
const setClips = (json: string): void => {
    dataInput.value = json;
};
/** Fire the DOM signal the timeline listens for (mirrors saveState → triggerChangeFor). */
const notify = (): void => triggerChangeFor(promptInput);

const regionCount = (): number =>
    document.querySelectorAll(`#${TIMELINE_BODY_ID} .vst-region`).length;

describe("videoStagesTimeline", () => {
    let timeline: VideoStagesTimeline | null = null;

    beforeEach(() => {
        jest.useFakeTimers();
        __resetPersistenceForTests();
        clearUiStateForTests();
        // Zoom/unit persist to localStorage; clear so one test's zoom never leaks into the next.
        localStorage.clear();
        setupBottomBar();
    });

    afterEach(() => {
        timeline?.dispose();
        timeline = null;
        jest.useRealTimers();
        document.body.innerHTML = "";
    });

    it("renders one region per clip on init", () => {
        mountState(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();

        expect(document.getElementById(TIMELINE_BODY_ID)).not.toBeNull();
        expect(regionCount()).toBe(1);
    });

    it("re-renders when the Data param dispatches a change signal (triggerChangeFor)", () => {
        mountState(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(1);

        setClips(makeClipsJson(2));
        notify();

        expect(regionCount()).toBe(2);
    });

    it("keeps refreshing after #input_prompt is replaced and init re-runs", () => {
        mountState(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(1);

        // Simulate a PARAM-panel rebuild that swaps #input_prompt for a fresh element.
        promptInput.remove();
        promptInput = mountPromptBox("");
        timeline.init(); // postParamBuildSteps re-runs init → re-bind to the new element.
        expect(regionCount()).toBe(1);

        setClips(makeClipsJson(3));
        notify();

        expect(regionCount()).toBe(3);
    });

    it("keeps the region count stable when only surrounding prompt prose changes", () => {
        mountState(makeClipsJson(2));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(2);

        // Editing global prose (no <videoclip> tags) does not change clip structure.
        promptInput.value = "a cinematic shot";
        triggerChangeFor(promptInput);

        expect(regionCount()).toBe(2);
    });

    it("toggles ruler/region labels between seconds and frames in-memory", () => {
        mountState(makeClipsJson(1, 2));

        timeline = videoStagesTimeline();
        timeline.init();

        const dur = (): string =>
            document
                .querySelector(`#${TIMELINE_BODY_ID} .vst-region-dur`)
                ?.textContent?.trim() ?? "";
        const toggle = (): HTMLButtonElement | null =>
            document.querySelector(
                `#${TIMELINE_BODY_ID} [data-vst-unit-toggle]`,
            );

        // Default is seconds; no DOM fps inputs => getRootDefaults falls back to 24fps.
        expect(dur()).toBe("2s");
        toggle()?.click();
        expect(dur()).toBe("48f"); // round(2s * 24fps)
        toggle()?.click();
        expect(dur()).toBe("2s");
    });

    it("applies slider zoom on change and stamps the new live zoom on the body", () => {
        mountState(makeClipsJson(1));
        timeline = videoStagesTimeline();
        timeline.init();
        const body = document.getElementById(TIMELINE_BODY_ID) as HTMLElement;
        expect(body.dataset.vstPps).toBe("44");

        const slider = body.querySelector<HTMLInputElement>(
            "[data-vst-zoom-slider]",
        );
        expect(slider).not.toBeNull();
        if (!slider) {
            return;
        }
        slider.value = "88";
        slider.dispatchEvent(new Event("change"));

        expect(body.dataset.vstPps).toBe("88");
        expect(body.querySelector("[data-vst-zoom-pct]")?.textContent).toBe(
            "200%",
        );
    });

    it("walks the input history end-to-end via the undo/redo toolbar buttons", () => {
        mountState(makeClipsJson(1));
        timeline = videoStagesTimeline();
        timeline.init();

        setClips(makeClipsJson(2));
        notify();
        expect(regionCount()).toBe(2);

        const body = (): HTMLElement =>
            document.getElementById(TIMELINE_BODY_ID) as HTMLElement;
        body().querySelector<HTMLButtonElement>("[data-vst-undo]")?.click();
        expect(regionCount()).toBe(1);
        // Re-query: the undo write re-rendered the toolbar.
        body().querySelector<HTMLButtonElement>("[data-vst-redo]")?.click();
        expect(regionCount()).toBe(2);
    });

    it("live-updates the readout's selected-clip segment on click-select without a re-render", async () => {
        mountState(makeClipsJson(2));
        timeline = videoStagesTimeline();
        timeline.init();
        const body = document.getElementById(TIMELINE_BODY_ID) as HTMLElement;

        const sel = body.querySelector<HTMLElement>("[data-vst-readout-sel]");
        expect(sel?.hidden).toBe(true);

        body.querySelector<HTMLElement>(
            '.vst-region[data-clip-idx="1"]',
        )?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
        // The readout poke runs on a microtask after linking's click handler.
        await Promise.resolve();

        expect(sel?.hidden).toBe(false);
        expect(sel?.textContent).toBe("clip 1");
    });

    it("polls for state drift as a fallback when no event fires", () => {
        mountState(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(1);

        // Value changes with no "input"/"change" event; the polling fallback catches it.
        setClips(makeClipsJson(4));
        jest.advanceTimersByTime(POLL_ADVANCE_MS);

        expect(regionCount()).toBe(4);
    });
});
