import {
    architectureForModel,
    loadAuthoritativeArchitectureCatalog,
} from "./architectures/catalog";
import { currentCapabilityViewResolver } from "./architectures/currentPolicy";
import { deriveAuthoringDiagnostics } from "./authoringDiagnostics";
import {
    injectTimelineTab,
    TIMELINE_BODY_ID,
    updateTimelineTabIndicator,
} from "./bottomTimelineTab";
import { createGestureRouter } from "./gestureRouter";
import { getVideoStagesHostBridge } from "./host";
import { buildDefaultClip } from "./normalization";
import {
    getClips,
    getState,
    getTimelineStore,
    saveClips,
    saveState,
} from "./persistence";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import { setSelection, subscribeSelection } from "./selection";
import type { UpdateMeta } from "./store";
import {
    getRootGeneratedEntryMode,
    isVideoStagesEnabled,
    readGlobalPrompt,
    setVideoStagesEnabled,
} from "./swarmInputs";
import { createTimelineAudioSegmentTrack } from "./timelineAudioSegmentTrack";
import { createTimelineAudioTrack } from "./timelineAudioTrack";
import { createTimelineBoundaryTrack } from "./timelineBoundaryTrack";
import { safeFps } from "./timelineDetail";
import { createTimelineDetailStrip } from "./timelineDetailStrip";
import { createTimelineHistory } from "./timelineHistory";
import { createTimelineHostLifecycle } from "./timelineHostLifecycle";
import { createTimelineLinking } from "./timelineLinking";
import { createTimelinePromptTrack } from "./timelinePromptTrack";
import { createTimelineReferencesTrack } from "./timelineReferencesTrack";
import { createTimelineRetakeTrack } from "./timelineRetakeTrack";
import { applySelectionHighlight } from "./timelineSelectionView";
import { renderTimeline } from "./timelineView";
import { createTimelineViewport } from "./timelineViewport";

export interface VideoStagesTimeline {
    init(): void;
    refresh(): void;
    dispose(): void;
}

export const videoStagesTimeline = (): VideoStagesTimeline => {
    let storeUnsub: (() => void) | null = null;
    let selectionUnsub: (() => void) | null = null;
    const timelineBody = (): HTMLElement | null =>
        document.getElementById(TIMELINE_BODY_ID);
    const scrollEl = (): HTMLElement | null =>
        timelineBody()?.querySelector<HTMLElement>(".vst-scroll") ?? null;
    const viewport = createTimelineViewport({
        refresh: () => refresh(),
        totalSeconds: () =>
            getClips().reduce(
                (sum, clip) => sum + Math.max(0, clip.duration || 0),
                0,
            ),
        timelineBody,
        scrollElement: scrollEl,
    });
    const detailStrip = createTimelineDetailStrip({
        isCollapsed: viewport.stripCollapsed,
        setCollapsed: viewport.setStripCollapsed,
    });
    const capabilities = currentCapabilityViewResolver;
    const linking = createTimelineLinking();
    const gestures = createGestureRouter();
    const retakeTrack = createTimelineRetakeTrack(capabilities);
    const promptTrack = createTimelinePromptTrack(capabilities);
    const audioTrack = createTimelineAudioTrack();
    const audioSegmentTrack = createTimelineAudioSegmentTrack(capabilities);
    const boundaryTrack = createTimelineBoundaryTrack();
    const referencesTrack = createTimelineReferencesTrack(capabilities);
    let addClipInFlight = false;

    // Open the timeline settings panel (docked in the detail strip) from the
    // topbar dims/fps chip: select "nothing" and force the strip open.
    const openSettings = (): void => {
        viewport.setStripCollapsed(false);
        setSelection({ kind: "none" });
        detailStrip.render();
    };

    const history = createTimelineHistory({
        // The canonical model contains everything VideoStages authors across
        // Data, clip-prompt, and UI-state carriers, including hue and
        // prompt-window IDs.
        read: () => JSON.stringify(getState()),
        write: (value) => {
            const state = JSON.parse(value) as ReturnType<typeof getState>;
            const expectedRevision = getTimelineStore().revision();
            saveState(state, {
                expectedRevision,
                notifyDomChange: isVideoStagesEnabled(),
                origin: "history",
            });
        },
    });
    const hostLifecycle = createTimelineHostLifecycle({
        refresh: () => refresh(),
        syncFromCarrier: () => getTimelineStore().syncFromCarrier(),
        undo: () => history.undo(),
        redo: () => history.redo(),
    });

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
            selEl.textContent = sel === null ? "" : `clip ${sel + 1}`;
        });
    };

    const addClipAfterCatalog = async (): Promise<void> => {
        try {
            // init starts this same coalesced request in the background. An
            // immediate empty-state click must wait for it before choosing the
            // first clip's model and architecture identity.
            await loadAuthoritativeArchitectureCatalog();
            const defaults = getRootDefaults();
            const defaultModel = getDefaultStageModel(defaults.modelValues);
            if (
                !defaultModel ||
                architectureForModel(defaults.modelCatalog, defaultModel) ===
                    null
            ) {
                getVideoStagesHostBridge().showError(
                    "VideoStages cannot add a clip because no supported video model is available.",
                );
                return;
            }

            const clips = getClips();
            const prev = clips[clips.length - 1] ?? null;
            // The new join (prev → new clip) mirrors the join between the
            // previous two clips, when one exists.
            if (prev && clips.length >= 2) {
                const prevJoin = clips[clips.length - 2];
                prev.boundaryOut = prevJoin.boundaryOut;
                prev.boundaryOutCarryAudio = prevJoin.boundaryOutCarryAudio;
                prev.boundaryOutOverlap = prevJoin.boundaryOutOverlap;
            }
            clips.push(
                buildDefaultClip(
                    () => defaults,
                    getDefaultStageModel,
                    false,
                    prev,
                ),
            );
            saveClips(clips, { origin: "timeline" });
        } catch (error) {
            console.warn("VideoStages: failed to add clip", error);
            getVideoStagesHostBridge().showError(
                "VideoStages could not add the clip. See the browser console for details.",
            );
        } finally {
            addClipInFlight = false;
        }
    };

    const addClip = (): void => {
        if (addClipInFlight) {
            return;
        }
        addClipInFlight = true;
        void addClipAfterCatalog();
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
            const globalPrompt = readGlobalPrompt();
            const architectureCatalog = getRootDefaults().modelCatalog;
            renderTimeline(body, clips, {
                fps: safeFps(state.fps),
                width: state.width,
                height: state.height,
                dimsExplicit: state.dimsExplicit,
                fpsExplicit: state.fpsExplicit,
                unit: viewport.unit(),
                pxPerSecond: viewport.pxPerSecond(),
                selectedIndex: linking.getSelectedIndex(),
                enabled,
                onToggleEnabled: setVideoStagesEnabled,
                onOpenSettings: () => openSettings(),
                onToggleUnit: viewport.toggleUnit,
                onAddClip: addClip,
                onZoomIn: viewport.zoomIn,
                onZoomOut: viewport.zoomOut,
                onZoomFit: viewport.zoomFit,
                onZoomSlider: viewport.setZoom,
                onZoomWheel: viewport.zoomWheel,
                onUndo: () => history.undo(),
                onRedo: () => history.redo(),
                globalPrompt,
                diagnostics: deriveAuthoringDiagnostics(clips, {
                    catalog: architectureCatalog,
                    generatedEntryMode: getRootGeneratedEntryMode(),
                }),
                capabilities: capabilities(),
            });
            viewport.restoreScroll(prevScrollLeft);
            linking.reapplySelection(body, clips.length);
            detailStrip.render(meta);
            applySelectionHighlight(body);
        } catch (error) {
            console.warn("VideoStages: timeline render failed", error);
        }
    };

    const refresh = (): void => renderAll();

    const init = (): void => {
        viewport.load();
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
        history.syncBaseline();
        hostLifecycle.bind();
        refresh();
        void loadAuthoritativeArchitectureCatalog().then((catalog) => {
            if (!catalog) {
                return;
            }
            getTimelineStore().invalidate();
            refresh();
        });
    };

    const dispose = (): void => {
        hostLifecycle.dispose();
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
    };

    return { init, refresh, dispose };
};
