import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { __resetPersistenceForTests } from "./persistence";
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

// The config rides in the <videostages> section of the positive prompt, not a dedicated param.
const section = (json: string): string => `<videostages>${json}`;

const addPromptInput = (json: string): HTMLTextAreaElement => {
    const input = document.createElement("textarea");
    input.id = "input_prompt";
    input.value = section(json);
    document.body.appendChild(input);
    return input;
};

const regionCount = (): number =>
    document.querySelectorAll(`#${TIMELINE_BODY_ID} .vst-region`).length;

describe("videoStagesTimeline", () => {
    let timeline: VideoStagesTimeline | null = null;

    beforeEach(() => {
        jest.useFakeTimers();
        __resetPersistenceForTests();
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
        addPromptInput(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();

        expect(document.getElementById(TIMELINE_BODY_ID)).not.toBeNull();
        expect(regionCount()).toBe(1);
    });

    it("re-renders when the prompt section dispatches a change signal (triggerChangeFor)", () => {
        const input = addPromptInput(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(1);

        // Mirrors saveState() → triggerChangeFor(): "input" + "change" dispatched on #input_prompt.
        input.value = section(makeClipsJson(2));
        triggerChangeFor(input);

        expect(regionCount()).toBe(2);
    });

    it("keeps refreshing after #input_prompt is replaced and init re-runs", () => {
        const input = addPromptInput(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(1);

        // Simulate a PARAM-panel rebuild that swaps #input_prompt for a fresh element.
        input.remove();
        const replacement = addPromptInput(makeClipsJson(1));
        timeline.init(); // postParamBuildSteps re-runs init → re-bind to the new element.
        expect(regionCount()).toBe(1);

        replacement.value = section(makeClipsJson(3));
        triggerChangeFor(replacement);

        expect(regionCount()).toBe(3);
    });

    it("does not re-render when only surrounding prompt prose changes", () => {
        const input = addPromptInput(makeClipsJson(2));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(2);

        // Prepend prose before the section; the extracted section body is unchanged.
        input.value = `a cinematic shot\n${section(makeClipsJson(2))}`;
        triggerChangeFor(input);

        expect(regionCount()).toBe(2);
    });

    it("toggles ruler/region labels between seconds and frames in-memory", () => {
        addPromptInput(makeClipsJson(1, 2));

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
        addPromptInput(makeClipsJson(1));
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
        const input = addPromptInput(makeClipsJson(1));
        timeline = videoStagesTimeline();
        timeline.init();

        input.value = section(makeClipsJson(2));
        triggerChangeFor(input);
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
        addPromptInput(makeClipsJson(2));
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

    it("polls for section drift as a fallback when no event fires", () => {
        const input = addPromptInput(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(1);

        // Value changes with no "input"/"change" event; the polling fallback catches it.
        input.value = section(makeClipsJson(4));
        jest.advanceTimersByTime(POLL_ADVANCE_MS);

        expect(regionCount()).toBe(4);
    });
});
