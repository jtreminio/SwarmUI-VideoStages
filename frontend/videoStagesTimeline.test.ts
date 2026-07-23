import {
    afterEach,
    beforeEach,
    describe,
    expect,
    it,
    jest,
} from "@jest/globals";
import { testArchitectureCatalog } from "./__test_helpers__/architectureFixtures";
import { minimalClip, minimalStage } from "./__test_helpers__/clipFixtures";
import {
    mountPromptBox,
    mountSelect,
    mountVideoStagesData,
} from "./__test_helpers__/dom";
import {
    __resetArchitectureCatalogForTests,
    ARCHITECTURE_CATALOG_API,
} from "./architectures/catalog";
import {
    setVideoStagesHostBridgeForTests,
    type VideoStagesHostBridge,
} from "./host";
import { createDefaultVideoStagesHostBridge } from "./host/defaultVideoStagesHostBridge";
import {
    __resetPersistenceForTests,
    getClips,
    getTimelineStore,
    saveClips,
} from "./persistence";
import { resetSelectionForTests, setSelection } from "./selection";
import { clearUiStateForTests } from "./uiState";
import {
    type VideoStagesTimeline,
    videoStagesTimeline,
} from "./videoStagesTimeline";

const TIMELINE_BODY_ID = "videostages-timeline-body";
// A little over the controller's 200ms poll interval, so one tick is guaranteed to fire.
const POLL_ADVANCE_MS = 250;
const modelGlobals = globalThis as unknown as {
    modelsHelpers?: {
        getDataFor: (
            category: string,
            modelName: string,
        ) => {
            modelClass: { compatClass: { id: string } };
        };
    };
};

const setupBottomBar = (): void => {
    const nav = document.createElement("ul");
    nav.id = "bottombartabcollection";
    document.body.appendChild(nav);

    const content = document.createElement("div");
    content.id = "t2i_bottom_bar_content";
    document.body.appendChild(content);
};

/** Every authored test clip is LTX 2.3; production rejects generic models. */
const mountRootDefaults = (): void => {
    mountSelect("input_videomodel", {
        value: "ltx-2.3.safetensors",
        options: ["ltx-2.3.safetensors"],
    });
    mountSelect("input_loras", { options: ["lora-x.safetensors"] });
    mountSelect("input_sampler", { options: ["euler"] });
    mountSelect("input_scheduler", { options: ["normal"] });
    mountSelect("input_refinerupscalemethod", { options: ["pixel-lanczos"] });
};

const makeClipsJson = (count: number, duration = 2): string =>
    JSON.stringify({
        schemaVersion: 3,
        clips: Array.from({ length: count }, () => ({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
            duration,
            stages: [
                {
                    model: "ltx-2.3.safetensors",
                    modelProfileId: "ltx-2.3",
                },
            ] as unknown[],
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
const setClips = (json: string): void => {
    dataInput.value = json;
};
/** Fire the DOM signal the timeline listens for (mirrors saveState → triggerChangeFor). */
const notify = (): void => triggerChangeFor(promptInput);

const regionCount = (): number =>
    document.querySelectorAll(`#${TIMELINE_BODY_ID} .vst-region`).length;
const flushMicrotasks = async (): Promise<void> => {
    for (let i = 0; i < 5; i++) {
        await Promise.resolve();
    }
};
describe("videoStagesTimeline", () => {
    let timeline: VideoStagesTimeline | null = null;

    beforeEach(() => {
        modelGlobals.modelsHelpers = {
            getDataFor: () => ({
                modelClass: { compatClass: { id: "ltxv2" } },
            }),
        };
        __resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson: async () => null,
        });
        jest.useFakeTimers();
        __resetPersistenceForTests();
        clearUiStateForTests();
        resetSelectionForTests();
        // Zoom/unit persist to localStorage; clear so one test's zoom never leaks into the next.
        localStorage.clear();
        setupBottomBar();
        mountRootDefaults();
    });

    afterEach(() => {
        timeline?.dispose();
        timeline = null;
        __resetArchitectureCatalogForTests();
        setVideoStagesHostBridgeForTests(null);
        jest.useRealTimers();
        resetSelectionForTests();
        document.body.innerHTML = "";
        delete modelGlobals.modelsHelpers;
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
                schemaVersion: 3,
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

    it("+ Clip copies the previous clip's base settings and mirrors the prior join", async () => {
        mountState(
            JSON.stringify({
                schemaVersion: 3,
                clips: [
                    {
                        duration: 2,
                        boundaryOut: "continue",
                        boundaryOutCarryAudio: true,
                        boundaryOutOverlap: 16,
                        stages: [{}] as unknown[],
                        refs: [] as unknown[],
                    },
                    {
                        duration: 3,
                        stages: [
                            {
                                sampler: "res_multistep",
                                scheduler: "sgm_uniform",
                                model: "ltx-2.3.safetensors",
                                modelProfileId: "ltx-2.3",
                                steps: 20,
                                cfgScale: 4,
                                loras: [{ name: "look", weight: 0.6 }],
                            },
                        ],
                        refs: [] as unknown[],
                    },
                ],
            }),
        );
        timeline = videoStagesTimeline();
        timeline.init();

        document
            .querySelector<HTMLButtonElement>(
                ".vst-topbar-tools [data-vst-add-clip]",
            )
            ?.click();
        await flushMicrotasks();

        const clips = getClips();
        expect(clips).toHaveLength(3);
        // The join into the new clip mirrors the join between the previous two.
        expect(clips[1].boundaryOut).toBe("continue");
        expect(clips[1].boundaryOutCarryAudio).toBe(true);
        expect(clips[1].boundaryOutOverlap).toBe(16);
        // The new clip inherits the previous clip's base settings.
        expect(clips[2].duration).toBe(3);
        const stage = clips[2].stages[0];
        expect(stage.sampler).toBe("res_multistep");
        expect(stage.scheduler).toBe("sgm_uniform");
        expect(stage.model).toBe("ltx-2.3.safetensors");
        expect(stage.steps).toBe(20);
        expect(stage.cfgScale).toBe(4);
        expect(stage.loras).toEqual([{ name: "look", weight: 0.6 }]);
    });

    it("adds the first clip after the backend catalog resolves even when the video-model dropdown is absent", async () => {
        document.getElementById("input_videomodel")?.remove();
        mountState(JSON.stringify({ schemaVersion: 3, clips: [] }));
        __resetArchitectureCatalogForTests();

        const architecture = testArchitectureCatalog().architectures[0];
        const dto = {
            architectures: [architecture],
            models: [
                {
                    modelName: "server-ltx-model.safetensors",
                    architectureId: "ltx2",
                    modelProfileId: "ltx-2.3",
                    compatId: "ltxv2",
                },
            ],
        };
        let resolveCatalog!: (value: unknown) => void;
        const requestJson = jest.fn<VideoStagesHostBridge["requestJson"]>(
            () =>
                new Promise((resolve) => {
                    resolveCatalog = resolve;
                }),
        );
        const showError = jest.fn();
        setVideoStagesHostBridgeForTests({
            ...createDefaultVideoStagesHostBridge(),
            requestJson,
            showError,
        });

        timeline = videoStagesTimeline();
        timeline.init();
        document
            .querySelector<HTMLButtonElement>(
                ".vst-topbar-tools [data-vst-add-clip]",
            )
            ?.click();

        expect(requestJson).toHaveBeenCalledTimes(1);
        expect(requestJson).toHaveBeenCalledWith(ARCHITECTURE_CATALOG_API);
        expect(getClips()).toHaveLength(0);

        resolveCatalog(dto);
        await flushMicrotasks();

        expect(showError).not.toHaveBeenCalled();
        expect(getClips()).toHaveLength(1);
        expect(getClips()[0]).toMatchObject({
            architecture: "ltx2",
            modelProfileId: "ltx-2.3",
            stages: [
                {
                    model: "server-ltx-model.safetensors",
                    modelProfileId: "ltx-2.3",
                },
            ],
        });
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

    it("undoes and redoes hues and prompt-window IDs through the repository", () => {
        mountEnabledToggle();
        mountState(JSON.stringify({ schemaVersion: 3, clips: [] }));
        saveClips(
            [
                minimalClip({
                    id: "clip-history",
                    hue: 25,
                    prompt: "first",
                    promptWindows: [
                        {
                            id: "window-first",
                            prompt: "first detail",
                            start: 0,
                            duration: 1,
                        },
                    ],
                    stages: [minimalStage({ id: "stage-history" })],
                }),
            ],
            { notifyDomChange: false },
        );
        timeline = videoStagesTimeline();
        timeline.init();

        const changed = getClips();
        changed[0].hue = 275;
        changed[0].prompt = "second";
        changed[0].promptWindows = [
            {
                id: "window-second",
                prompt: "second detail",
                start: 1,
                duration: 1,
            },
        ];
        saveClips(changed, { notifyDomChange: false });
        const historyOrigins: string[] = [];
        getTimelineStore().subscribe((_state, meta) => {
            historyOrigins.push(meta.origin);
        });

        const body = (): HTMLElement =>
            document.getElementById(TIMELINE_BODY_ID) as HTMLElement;
        body().querySelector<HTMLButtonElement>("[data-vst-undo]")?.click();
        expect(historyOrigins).toEqual(["history"]);
        expect(getClips()[0]).toMatchObject({
            hue: 25,
            prompt: "first",
            promptWindows: [
                {
                    id: "window-first",
                    prompt: "first detail",
                    start: 0,
                    duration: 1,
                },
            ],
        });
        expect(promptInput.value).toContain("<videoclip[0]>first");
        expect(
            JSON.parse(localStorage.getItem("videostages_ui_state") ?? "{}")
                .clips[0],
        ).toMatchObject({
            id: "clip-history",
            hue: 25,
            promptWindows: [{ id: "window-first" }],
        });

        body().querySelector<HTMLButtonElement>("[data-vst-redo]")?.click();
        expect(historyOrigins).toEqual(["history", "history"]);
        expect(getClips()[0]).toMatchObject({
            hue: 275,
            prompt: "second",
            promptWindows: [
                {
                    id: "window-second",
                    prompt: "second detail",
                    start: 1,
                    duration: 1,
                },
            ],
        });
        expect(
            JSON.parse(localStorage.getItem("videostages_ui_state") ?? "{}")
                .clips[0],
        ).toMatchObject({
            id: "clip-history",
            hue: 275,
            promptWindows: [{ id: "window-second" }],
        });
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
        expect(sel?.textContent).toBe("clip 2");
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
                schemaVersion: 3,
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

    it("repaints stage dropdowns when the host refreshes models/loras", () => {
        mountState(makeClipsJson(1));
        mountEnabledToggle();
        const hostModel = document.getElementById(
            "input_videomodel",
        ) as HTMLSelectElement;
        const alternate = document.createElement("option");
        alternate.value = "ltx-2.3-alt.safetensors";
        alternate.text = alternate.value;
        hostModel.appendChild(alternate);

        timeline = videoStagesTimeline();
        timeline.init();
        setSelection({ kind: "clip", clipIdx: 0, stageIdx: 0 });

        const modelOptions = (): string[] => {
            const selects = Array.from(
                document.querySelectorAll<HTMLSelectElement>(
                    ".vst-detail select",
                ),
            );
            const model = selects.find((s) =>
                Array.from(s.options).some(
                    (o) => o.value === "ltx-2.3.safetensors",
                ),
            );
            return Array.from(model?.options ?? []).map((o) => o.value);
        };

        expect(modelOptions()).toEqual([
            "ltx-2.3.safetensors",
            "ltx-2.3-alt.safetensors",
        ]);

        // Host refresh button repopulates the core dropdown, then runs the
        // refreshParamsExtra callbacks. Our hook defers a repaint one tick.
        const newOption = document.createElement("option");
        newOption.value = "ltx-2.3-refreshed.safetensors";
        newOption.text = newOption.value;
        hostModel.appendChild(newOption);

        const extras = refreshParamsExtra as (() => unknown)[];
        expect(extras.length).toBe(1);
        for (const cb of extras) {
            cb();
        }
        jest.advanceTimersByTime(1);

        expect(modelOptions()).toContain("ltx-2.3-refreshed.safetensors");
    });

    it("removes its host-refresh hook on dispose", () => {
        mountState(makeClipsJson(1));
        mountEnabledToggle();

        timeline = videoStagesTimeline();
        timeline.init();
        expect((refreshParamsExtra as (() => unknown)[]).length).toBe(1);

        timeline.dispose();
        timeline = null;
        expect((refreshParamsExtra as (() => unknown)[]).length).toBe(0);
    });
});
