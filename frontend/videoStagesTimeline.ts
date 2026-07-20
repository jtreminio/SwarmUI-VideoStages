import {
    injectTimelineTab,
    TIMELINE_BODY_ID,
    updateTimelineTabIndicator,
} from "./bottomTimelineTab";
import { createGestureRouter } from "./gestureRouter";
import { buildDefaultClip } from "./normalization";
import { getClips, getState, getTimelineStore, saveClips } from "./persistence";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import type { UpdateMeta } from "./store";
import {
    getGroupToggle,
    getPromptInput,
    isVideoStagesEnabled,
    readCarrierSnapshot,
    readGlobalPrompt,
    restoreCarrierSnapshot,
    setVideoStagesEnabled,
} from "./swarmInputs";
import { createTimelineAudioSegmentTrack } from "./timelineAudioSegmentTrack";
import { createTimelineAudioTrack } from "./timelineAudioTrack";
import { createTimelineBoundaryTrack } from "./timelineBoundaryTrack";
import { safeFps, type TimelineUnit } from "./timelineDetail";
import { createTimelineDetailStrip } from "./timelineDetailStrip";
import { createTimelineHistory } from "./timelineHistory";
import { createTimelineLinking } from "./timelineLinking";
import { createTimelinePromptTrack } from "./timelinePromptTrack";
import { createTimelineReferencesTrack } from "./timelineReferencesTrack";
import { createTimelineRetakeTrack } from "./timelineRetakeTrack";
import { applySelectionHighlight } from "./timelineSelectionView";
import {
    clampPxPerSecond,
    computeFitPxPerSecond,
    DEFAULT_PX_PER_SECOND,
    renderTimeline,
    TRACK_HEADER_W_PX,
    ZOOM_FACTOR,
    zoomAnchorScrollLeft,
    zoomAnchorTime,
} from "./timelineView";
import { setSelection, subscribeSelection } from "./uiState";

export interface VideoStagesTimeline {
    init(): void;
    refresh(): void;
    dispose(): void;
}

const INPUT_SYNC_INTERVAL_MS = 200;

export const videoStagesTimeline = (): VideoStagesTimeline => {
    let boundInput: HTMLInputElement | HTMLTextAreaElement | null = null;
    let boundToggle: HTMLInputElement | null = null;
    let inputSyncInterval: ReturnType<typeof setInterval> | null = null;
    let storeUnsub: (() => void) | null = null;
    let unit: TimelineUnit = "seconds";
    let pxPerSecond = DEFAULT_PX_PER_SECOND;
    // Zoom scale of the LAST completed render — lets the scroll restore detect
    // zoom changes and re-anchor by time instead of raw pixels.
    let lastRenderedPxPerSecond = 0;
    let stripCollapsed = false;
    let selectionUnsub: (() => void) | null = null;
    const detailStrip = createTimelineDetailStrip({
        isCollapsed: () => stripCollapsed,
        setCollapsed: (collapsed) => {
            stripCollapsed = collapsed;
            saveViewState();
        },
    });
    const linking = createTimelineLinking();
    const gestures = createGestureRouter();
    const retakeTrack = createTimelineRetakeTrack();
    const promptTrack = createTimelinePromptTrack();
    const audioTrack = createTimelineAudioTrack();
    const audioSegmentTrack = createTimelineAudioSegmentTrack();
    const boundaryTrack = createTimelineBoundaryTrack();
    const referencesTrack = createTimelineReferencesTrack();

    // Open the timeline settings panel (docked in the detail strip) from the
    // topbar dims/fps chip: select "nothing" and force the strip open.
    const openSettings = (): void => {
        stripCollapsed = false;
        saveViewState();
        setSelection({ kind: "none" });
        detailStrip.render();
    };

    const history = createTimelineHistory({
        read: () => readCarrierSnapshot(),
        write: (value) => restoreCarrierSnapshot(value),
    });

    const VIEW_STATE_KEY = "videostages.timeline.viewState";
    const loadViewState = (): void => {
        try {
            const raw = localStorage.getItem(VIEW_STATE_KEY);
            if (!raw) {
                return;
            }
            const parsed = JSON.parse(raw) as {
                pxPerSecond?: unknown;
                unit?: unknown;
                stripCollapsed?: unknown;
            };
            if (typeof parsed.pxPerSecond === "number") {
                pxPerSecond = clampPxPerSecond(parsed.pxPerSecond);
            }
            if (parsed.unit === "frames" || parsed.unit === "seconds") {
                unit = parsed.unit;
            }
            if (typeof parsed.stripCollapsed === "boolean") {
                stripCollapsed = parsed.stripCollapsed;
            }
        } catch {}
    };
    const saveViewState = (): void => {
        try {
            localStorage.setItem(
                VIEW_STATE_KEY,
                JSON.stringify({
                    pxPerSecond,
                    unit,
                    stripCollapsed,
                }),
            );
        } catch {}
    };

    const toggleUnit = (): void => {
        unit = unit === "seconds" ? "frames" : "seconds";
        saveViewState();
        refresh();
    };

    const timelineBody = (): HTMLElement | null =>
        document.getElementById(TIMELINE_BODY_ID);

    // The left dock (`.vst-detail`) is a sibling of the tracks body inside the
    // `.vst-timeline` shell, created here so renderTimeline's innerHTML wipe of
    // the tracks body never touches it. Rendered into by the detail strip.
    const ensureDock = (body: HTMLElement): HTMLElement => {
        const shell = body.parentElement;
        if (!shell) {
            throw new Error("timeline body has no shell parent");
        }
        let dock = shell.querySelector<HTMLElement>(":scope > .vst-detail");
        if (!dock) {
            dock = document.createElement("div");
            dock.className = "vst-detail";
            shell.insertBefore(dock, body);
        }
        return dock;
    };
    const scrollEl = (): HTMLElement | null =>
        timelineBody()?.querySelector<HTMLElement>(".vst-scroll") ?? null;

    const setZoom = (value: number): void => {
        pxPerSecond = clampPxPerSecond(value);
        saveViewState();
        refresh();
    };
    const zoomIn = (): void => setZoom(pxPerSecond * ZOOM_FACTOR);
    const zoomOut = (): void => setZoom(pxPerSecond / ZOOM_FACTOR);
    const zoomFit = (): void => {
        const totalSeconds = getClips().reduce(
            (sum, clip) => sum + Math.max(0, clip.duration || 0),
            0,
        );
        const width =
            scrollEl()?.clientWidth ?? timelineBody()?.clientWidth ?? 0;
        setZoom(
            computeFitPxPerSecond(totalSeconds, width, TRACK_HEADER_W_PX + 24),
        );
    };
    const zoomWheel = (factor: number, clientX: number): void => {
        const scroll = scrollEl();
        if (!scroll || pxPerSecond <= 0) {
            setZoom(pxPerSecond * factor);
            return;
        }
        const offsetX = clientX - scroll.getBoundingClientRect().left;
        const timeAtPointer = zoomAnchorTime(
            offsetX,
            scroll.scrollLeft,
            pxPerSecond,
        );
        setZoom(pxPerSecond * factor);
        const fresh = scrollEl();
        if (fresh) {
            fresh.scrollLeft = zoomAnchorScrollLeft(
                timeAtPointer,
                pxPerSecond,
                offsetX,
            );
        }
    };

    const onBodyClickSyncReadout = (): void => {
        void Promise.resolve().then(() => {
            const body = timelineBody();
            if (!body) {
                return;
            }
            const sel = linking.getSelectedIndex();
            const selEl = body.querySelector<HTMLElement>(
                "[data-vst-readout-sel]",
            );
            const dotEl = body.querySelector<HTMLElement>(
                "[data-vst-readout-sel-dot]",
            );
            if (!selEl || !dotEl) {
                return;
            }
            selEl.hidden = sel === null;
            dotEl.hidden = sel === null;
            selEl.textContent = sel === null ? "" : `clip ${sel}`;
        });
    };

    const addClip = (): void => {
        const clips = getClips();
        clips.push(buildDefaultClip(getRootDefaults, getDefaultStageModel));
        saveClips(clips, { origin: "timeline" });
    };

    /**
     * Repaint everything from current state. Driven by the store subscription
     * for state changes (meta says who committed) and called directly (no
     * meta) for view-only changes: zoom, unit, strip collapse,
     * enable toggle — none of which touch the carriers.
     */
    const renderAll = (meta?: UpdateMeta): void => {
        const enabled = isVideoStagesEnabled();
        updateTimelineTabIndicator(enabled);
        const body = document.getElementById(TIMELINE_BODY_ID);
        if (!body) {
            return;
        }
        // renderTimeline wipes the body's innerHTML, destroying the scroll
        // container — without this, every state commit (drag-drop, edits)
        // snaps a wide timeline back to the far left. External (host-side)
        // replacements — loading a different config, prompt-box pastes — do
        // NOT restore: the old position is meaningless for the new content.
        const prevScrollLeft =
            meta?.origin === "external" ? 0 : (scrollEl()?.scrollLeft ?? 0);
        try {
            const state = getState();
            const clips = state.clips;
            renderTimeline(body, clips, {
                fps: safeFps(state.fps),
                width: state.width,
                height: state.height,
                dimsExplicit: state.dimsExplicit,
                fpsExplicit: state.fpsExplicit,
                unit,
                pxPerSecond,
                selectedIndex: linking.getSelectedIndex(),
                enabled,
                onToggleEnabled: setVideoStagesEnabled,
                onOpenSettings: () => openSettings(),
                onToggleUnit: toggleUnit,
                onAddClip: addClip,
                onZoomIn: zoomIn,
                onZoomOut: zoomOut,
                onZoomFit: zoomFit,
                onZoomSlider: setZoom,
                onZoomWheel: zoomWheel,
                onUndo: () => history.undo(),
                onRedo: () => history.redo(),
                globalPrompt: readGlobalPrompt(),
            });
            if (prevScrollLeft > 0) {
                // If the zoom changed since the last render (toolbar buttons,
                // slider, fit), a raw pixel restore would land on a different
                // TIME — re-anchor the left edge's time under the new scale.
                // (zoomWheel then overrides with its own pointer anchor.)
                const target =
                    lastRenderedPxPerSecond > 0 &&
                    lastRenderedPxPerSecond !== pxPerSecond
                        ? zoomAnchorScrollLeft(
                              zoomAnchorTime(
                                  TRACK_HEADER_W_PX,
                                  prevScrollLeft,
                                  lastRenderedPxPerSecond,
                              ),
                              pxPerSecond,
                              TRACK_HEADER_W_PX,
                          )
                        : prevScrollLeft;
                const fresh = scrollEl();
                if (fresh) {
                    fresh.scrollLeft = target;
                }
            }
            lastRenderedPxPerSecond = pxPerSecond;
            linking.reapplySelection(body, clips.length);
            detailStrip.render(meta);
            applySelectionHighlight(body);
        } catch (error) {
            console.warn("VideoStages: timeline render failed", error);
        }
    };

    const refresh = (): void => renderAll();

    const onInputChanged = (): void => {
        getTimelineStore().syncFromCarrier();
    };

    const bindInputListener = (): void => {
        const input = getPromptInput();
        if (!input || input === boundInput) {
            return;
        }
        if (boundInput) {
            boundInput.removeEventListener("input", onInputChanged);
            boundInput.removeEventListener("change", onInputChanged);
        }
        input.addEventListener("input", onInputChanged);
        input.addEventListener("change", onInputChanged);
        boundInput = input;
    };

    const onEnabledToggled = (): void => {
        refresh();
    };

    const bindToggleListener = (): void => {
        const toggle = getGroupToggle();
        if (!toggle || toggle === boundToggle) {
            return;
        }
        if (boundToggle) {
            boundToggle.removeEventListener("change", onEnabledToggled);
        }
        toggle.addEventListener("change", onEnabledToggled);
        boundToggle = toggle;
    };

    const startInputSync = (): void => {
        if (inputSyncInterval) {
            return;
        }
        // Catches carrier writers that fire no events (host scripts poking the
        // inputs) and inherited-dims changes — both are part of the store's
        // change token, so one sync call covers them.
        inputSyncInterval = setInterval(() => {
            getTimelineStore().syncFromCarrier();
        }, INPUT_SYNC_INTERVAL_MS);
    };

    const onKeydown = (event: KeyboardEvent): void => {
        if (!(event.ctrlKey || event.metaKey)) {
            return;
        }
        const key = event.key.toLowerCase();
        const isUndo = key === "z" && !event.shiftKey;
        const isRedo = (key === "z" && event.shiftKey) || key === "y";
        if (!isUndo && !isRedo) {
            return;
        }
        const active = document.activeElement;
        const inTextField =
            active instanceof HTMLInputElement ||
            active instanceof HTMLTextAreaElement ||
            (active instanceof HTMLElement && active.isContentEditable);
        if (inTextField || !isVideoStagesEnabled()) {
            return;
        }
        if (isUndo ? history.undo() : history.redo()) {
            event.preventDefault();
        }
    };

    const init = (): void => {
        loadViewState();
        injectTimelineTab();
        const body = document.getElementById(TIMELINE_BODY_ID);
        if (body) {
            // Press-drag priority lives in the gesture router's table
            // (retake 50 > audio-segment 40 > prompt-track 20 >
            // linking 10), not in attach order.
            retakeTrack.attach(body, gestures);
            audioSegmentTrack.attach(body, gestures);
            linking.attach(body, gestures);
            promptTrack.attach(body, gestures);
            audioTrack.attach(body);
            boundaryTrack.attach(body);
            referencesTrack.attach(body, gestures);
            detailStrip.attach(body, ensureDock(body));
            // ORDER MATTERS: the router's capture-phase listeners must attach
            // AFTER the detail strip's, so the strip's chip handler runs first
            // and its stopPropagation claim is visible to the router.
            gestures.attach(body);
            body.removeEventListener("click", onBodyClickSyncReadout);
            body.addEventListener("click", onBodyClickSyncReadout);
        }
        selectionUnsub?.();
        selectionUnsub = subscribeSelection(() => {
            const el = document.getElementById(TIMELINE_BODY_ID);
            if (el) {
                applySelectionHighlight(el);
            }
        });
        // init re-runs on every host param rebuild: dropdown lists and root
        // defaults may have changed under the cached parse, so force a re-read.
        const store = getTimelineStore();
        store.invalidate();
        storeUnsub?.();
        storeUnsub = store.subscribe((_state, meta) => {
            // Capture BEFORE painting (the order refresh() used to do it in);
            // capture reads the carriers directly, which the store wrote
            // before notifying, so every commit/external change lands exactly
            // one history point.
            history.capture();
            renderAll(meta);
        });
        bindInputListener();
        bindToggleListener();
        history.syncBaseline();
        document.removeEventListener("keydown", onKeydown);
        document.addEventListener("keydown", onKeydown);
        startInputSync();
        refresh();
    };

    const dispose = (): void => {
        if (inputSyncInterval) {
            clearInterval(inputSyncInterval);
            inputSyncInterval = null;
        }
        if (boundInput) {
            boundInput.removeEventListener("input", onInputChanged);
            boundInput.removeEventListener("change", onInputChanged);
            boundInput = null;
        }
        if (boundToggle) {
            boundToggle.removeEventListener("change", onEnabledToggled);
            boundToggle = null;
        }
        retakeTrack.dispose();
        audioSegmentTrack.dispose();
        linking.dispose();
        promptTrack.dispose();
        gestures.dispose();
        audioTrack.dispose();
        boundaryTrack.dispose();
        referencesTrack.dispose();
        detailStrip.dispose();
        selectionUnsub?.();
        selectionUnsub = null;
        storeUnsub?.();
        storeUnsub = null;
        const body = document.getElementById(TIMELINE_BODY_ID);
        body?.removeEventListener("click", onBodyClickSyncReadout);
        document.removeEventListener("keydown", onKeydown);
    };

    return { init, refresh, dispose };
};
