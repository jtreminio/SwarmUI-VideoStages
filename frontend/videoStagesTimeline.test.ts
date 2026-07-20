import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { mountPromptBox, mountVideoStagesData } from "./__test_helpers__/dom";
import { __resetPersistenceForTests, getClips, saveClips } from "./persistence";
import {
    clearUiStateForTests,
    resetSelectionForTests,
    setSelection,
} from "./uiState";
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
        resetSelectionForTests();
        // Zoom/unit persist to localStorage; clear so one test's zoom never leaks into the next.
        localStorage.clear();
        setupBottomBar();
    });

    afterEach(() => {
        timeline?.dispose();
        timeline = null;
        jest.useRealTimers();
        resetSelectionForTests();
        document.body.innerHTML = "";
    });

    // Group toggle must be present + checked so live-apply writes actually
    // dispatch the DOM-change signal that drives the real refresh path.
    const mountEnabledToggle = (): void => {
        const cb = document.createElement("input");
        cb.type = "checkbox";
        cb.id = "input_group_content_videostages_toggle";
        cb.checked = true;
        document.body.appendChild(cb);
    };

    it("renders one region per clip on init", () => {
        mountState(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();

        expect(document.getElementById(TIMELINE_BODY_ID)).not.toBeNull();
        expect(regionCount()).toBe(1);
    });

    it("shows a green check on the Timeline tab nav while enabled, and clears it on disable", () => {
        mountEnabledToggle();
        mountState(makeClipsJson(1));

        timeline = videoStagesTimeline();
        timeline.init();

        const navLink = document.querySelector(
            'a[href="#VideoStages-Timeline-Tab"]',
        );
        expect(navLink).not.toBeNull();
        expect(navLink?.querySelector(".vst-tab-check")?.textContent).toBe("✓");

        const toggle = document.getElementById(
            "input_group_content_videostages_toggle",
        ) as HTMLInputElement;
        toggle.checked = false;
        toggle.dispatchEvent(new Event("change", { bubbles: true }));

        expect(navLink?.querySelector(".vst-tab-check")).toBeNull();
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

    it("a drag starting on a stage chip never becomes a region reorder", () => {
        // The detail strip's capture-phase chip handler claims chip presses via
        // stopPropagation; the gesture router (also capture-phase, attached
        // later) must honor that claim — a sibling capture listener would
        // otherwise still fire and hand the press to the region-drag route.
        mountEnabledToggle();
        mountState(
            JSON.stringify({
                clips: [
                    { duration: 2, stages: [{}], refs: [] },
                    { duration: 4, stages: [{}], refs: [] },
                ],
            }),
        );
        timeline = videoStagesTimeline();
        timeline.init();
        const chip = document.querySelector<HTMLElement>(
            '[data-vst-stage][data-clip-idx="0"]',
        );
        if (!chip) {
            throw new Error("stage chip not found");
        }
        const body = document.getElementById(TIMELINE_BODY_ID) as HTMLElement;
        chip.dispatchEvent(
            new MouseEvent("mousedown", { bubbles: true, clientX: 10 }),
        );
        document.dispatchEvent(
            new MouseEvent("mousemove", { bubbles: true, clientX: 60 }),
        );
        expect(body.classList.contains("vst-dragging")).toBe(false);
        document.dispatchEvent(
            new MouseEvent("mouseup", { bubbles: true, clientX: 60 }),
        );
        const stored = JSON.parse(dataInput.value) as {
            clips: { duration: number }[];
        };
        expect(stored.clips.map((c) => c.duration)).toEqual([2, 4]);
    });

    it("repaints the tracks exactly once for a single committed save", () => {
        // Enabled: the save dispatches the host change signal, whose synchronous
        // echo into our own carrier listener must NOT cause a second repaint on
        // top of the store's commit notification.
        mountEnabledToggle();
        mountState(makeClipsJson(1));
        timeline = videoStagesTimeline();
        timeline.init();
        expect(regionCount()).toBe(1);

        // Count full tracks repaints: renderTimeline performs exactly one
        // innerHTML write on the tracks body per render.
        const body = document.getElementById(TIMELINE_BODY_ID) as HTMLElement;
        const proto = Object.getOwnPropertyDescriptor(
            Element.prototype,
            "innerHTML",
        ) as PropertyDescriptor;
        let repaints = 0;
        Object.defineProperty(body, "innerHTML", {
            configurable: true,
            get() {
                return (proto.get as (this: Element) => string).call(this);
            },
            set(value: string) {
                repaints++;
                (proto.set as (this: Element, v: string) => void).call(
                    this,
                    value,
                );
            },
        });

        const clips = getClips();
        clips.push(structuredClone(clips[0]));
        saveClips(clips, { origin: "linking" });

        expect(repaints).toBe(1);
        expect(regionCount()).toBe(2);
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

    it("does not repaint the tracks while typing in a dock field, exactly once on blur", () => {
        // Enabled so live-apply writes dispatch the DOM-change refresh signal.
        mountEnabledToggle();
        mountState(makeClipsJson(1));
        timeline = videoStagesTimeline();
        timeline.init();

        // Show the clip's major-prompt editor in the left dock.
        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const region = document.querySelector<HTMLElement>(
            `#${TIMELINE_BODY_ID} .vst-region`,
        );
        const editor = document.querySelector<HTMLTextAreaElement>(
            ".vst-detail .vst-detail-prompt",
        );
        if (!region || !editor) {
            throw new Error("region or dock editor missing");
        }

        editor.focus();
        editor.value = "sunset over rolling hills";
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        // Poll interval + debounce both elapse: still ZERO track repaints, and
        // the carrier is untouched (so the `<`-help dropdown never fires either).
        jest.advanceTimersByTime(POLL_ADVANCE_MS * 3);
        expect(document.querySelector(`#${TIMELINE_BODY_ID} .vst-region`)).toBe(
            region,
        );
        expect(promptInput.value).not.toContain("sunset over rolling hills");

        // Blur out of the dock: exactly one repaint (fresh region node) and the
        // carrier now carries the typed prompt.
        editor.dispatchEvent(
            new FocusEvent("focusout", {
                bubbles: true,
                relatedTarget: document.body,
            }),
        );
        const after = document.querySelector<HTMLElement>(
            `#${TIMELINE_BODY_ID} .vst-region`,
        );
        expect(after).not.toBe(region);
        expect(promptInput.value).toContain("sunset over rolling hills");
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

    it("renders no playhead (removed until an existing-video workflow needs it)", () => {
        mountState(makeClipsJson(2, 2));
        timeline = videoStagesTimeline();
        timeline.init();
        const body = document.getElementById(TIMELINE_BODY_ID) as HTMLElement;
        expect(body.querySelector("[data-vst-playhead]")).toBeNull();
        expect(body.querySelector("[data-vst-playhead-handle]")).toBeNull();
        expect(body.querySelector("[data-vst-readout-head]")).toBeNull();
        // Pressing the ruler is inert — no gesture route claims it.
        const ruler = body.querySelector<HTMLElement>(".vst-ruler");
        ruler?.dispatchEvent(
            new MouseEvent("mousedown", { bubbles: true, clientX: 212 }),
        );
        document.dispatchEvent(new MouseEvent("mouseup", { clientX: 212 }));
        const stored = JSON.parse(
            localStorage.getItem("videostages.timeline.viewState") ?? "{}",
        );
        expect(stored.playheadSeconds).toBeUndefined();
    });

    // Regression: the debounced live-apply flush → saveClips → prompt-input
    // change → refresh → renderTimeline + dock re-render used to steal the caret
    // out of the dock textarea. The dock must keep focus (and caret) through the
    // real refresh path, including a second render arriving back-to-back.
    it("keeps focus + caret in the MAJOR prompt textarea through the real refresh", () => {
        mountEnabledToggle();
        mountVideoStagesData(makeClipsJson(1, 10));
        mountPromptBox("");
        timeline = videoStagesTimeline();
        timeline.init();

        setSelection({ kind: "prompt-major", clipIdx: 0 });
        const editor = document.querySelector<HTMLTextAreaElement>(
            'textarea[data-vst-focus-key="prompt-major"]',
        );
        if (!editor) {
            throw new Error("major textarea missing");
        }
        editor.focus();
        editor.value = "hello world";
        editor.setSelectionRange(11, 11);
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(250);
        // A second render lands right after the first restored focus.
        timeline.refresh();

        const active = document.activeElement as HTMLTextAreaElement | null;
        expect(active?.getAttribute("data-vst-focus-key")).toBe("prompt-major");
        expect(active?.selectionStart).toBe(11);
    });

    it("keeps focus + caret in a MINOR relay textarea through the real refresh", () => {
        mountEnabledToggle();
        mountVideoStagesData(
            JSON.stringify({
                clips: [{ duration: 10, stages: [{}], refs: [] }],
            }),
        );
        // A relay window rides in the prompt box as a <videoclip[0]:S-E> tag.
        mountPromptBox("<videoclip[0]:2-5>old");
        timeline = videoStagesTimeline();
        timeline.init();

        setSelection({ kind: "prompt-minor", clipIdx: 0, windowIdx: 0 });
        const editor = document.querySelector<HTMLTextAreaElement>(
            'textarea[data-vst-focus-key="minor-0"]',
        );
        if (!editor) {
            throw new Error("minor textarea missing");
        }
        editor.focus();
        editor.value = "a red car";
        editor.setSelectionRange(9, 9);
        editor.dispatchEvent(new Event("input", { bubbles: true }));
        jest.advanceTimersByTime(250);
        timeline.refresh();

        const active = document.activeElement as HTMLTextAreaElement | null;
        expect(active?.getAttribute("data-vst-focus-key")).toBe("minor-0");
        expect(active?.selectionStart).toBe(9);
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
