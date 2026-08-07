import {
    renderBlockingArchitectureCatalogStatus,
    renderRetainedArchitectureCatalogStatus,
} from "./architectureCatalogStatusView";
import {
    architectureForModel,
    getArchitectureCatalogSnapshot,
    loadAuthoritativeArchitectureCatalog,
    refreshAuthoritativeArchitectureCatalog,
    subscribeArchitectureCatalog,
} from "./architectures/catalog";
import { deriveAuthoringDiagnostics } from "./authoringDiagnostics";
import { captureAuthoringTransactionSnapshot } from "./authoringSnapshot";
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
} from "./persistence/repository";
import { getDefaultStageModel } from "./rootDefaults";
import { setSelection, subscribeSelection } from "./selection";
import type { UpdateMeta } from "./store";
import {
    isVideoStagesEnabled,
    readGlobalPrompt,
    setVideoStagesEnabled,
} from "./swarmInputs";
import { createTimelineAudioSpanTrack } from "./timelineAudioSpanTrack";
import { safeFps } from "./timelineDetail";
import { createTimelineDetailStrip } from "./timelineDetailStrip";
import { createTimelineHistory } from "./timelineHistory";
import { createTimelineHostLifecycle } from "./timelineHostLifecycle";
import { createTimelineLinking } from "./timelineLinking";
import { createTimelinePromptTrack } from "./timelinePromptTrack";
import { createTimelineReferencesTrack } from "./timelineReferencesTrack";
import { createTimelineRetakeTrack } from "./timelineRetakeTrack";
import { createTimelineSelectionTracks } from "./timelineSelectionTracks";
import { applySelectionHighlight } from "./timelineSelectionView";
import {
    resolveTimelineTiming,
    timelineDisplaySeconds,
} from "./timelineTiming";
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
    let catalogUnsub: (() => void) | null = null;
    const timelineBody = (): HTMLElement | null =>
        document.getElementById(TIMELINE_BODY_ID);
    const scrollEl = (): HTMLElement | null =>
        timelineBody()?.querySelector<HTMLElement>(".vst-scroll") ?? null;
    const capabilities = () =>
        captureAuthoringTransactionSnapshot().capabilities;
    const viewport = createTimelineViewport({
        refresh: () => refresh(),
        totalSeconds: () => {
            const state = getState();
            const timing = resolveTimelineTiming(
                state.clips,
                safeFps(state.fps),
                capabilities(),
            );
            return timelineDisplaySeconds(state.clips, timing);
        },
        timelineBody,
        scrollElement: scrollEl,
    });
    const detailStrip = createTimelineDetailStrip();
    const linking = createTimelineLinking();
    const gestures = createGestureRouter();
    const retakeTrack = createTimelineRetakeTrack(capabilities);
    const promptTrack = createTimelinePromptTrack(capabilities);
    const audioSpanTrack = createTimelineAudioSpanTrack(capabilities);
    const selectionTracks = createTimelineSelectionTracks();
    const referencesTrack = createTimelineReferencesTrack(
        captureAuthoringTransactionSnapshot,
    );
    let addClipInFlight = false;
    let historyNeedsRebase = true;
    const hasAuthoritativeCatalog = (): boolean =>
        getArchitectureCatalogSnapshot().catalog !== null;

    // Open the timeline settings panel (docked in the detail strip) from the
    // topbar dims/fps chip.
    const openSettings = (): void => {
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
    const rebaseHistoryIfReady = (): void => {
        if (!hasAuthoritativeCatalog()) {
            historyNeedsRebase = true;
            return;
        }
        if (historyNeedsRebase) {
            history.rebase();
            historyNeedsRebase = false;
        }
    };
    const hostLifecycle = createTimelineHostLifecycle({
        refresh: () => refresh(),
        refreshCatalog: () => {
            requestArchitectureCatalog(true);
        },
        syncFromCarrier: () => {
            if (!hasAuthoritativeCatalog()) {
                return;
            }
            rebaseHistoryIfReady();
            getTimelineStore().syncFromCarrier();
        },
        flushPending: () => {
            if (hasAuthoritativeCatalog()) {
                detailStrip.flushPending();
            }
        },
        undo: () => hasAuthoritativeCatalog() && history.undo(),
        redo: () => hasAuthoritativeCatalog() && history.redo(),
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
            selEl.textContent = sel === null ? "" : `clip ${sel}`;
        });
    };

    const addClipAfterCatalog = async (): Promise<void> => {
        try {
            // init starts this same coalesced request in the background. An
            // immediate empty-state click must wait for it before choosing the
            // first clip's model and architecture identity.
            await loadAuthoritativeArchitectureCatalog();
            const { defaults } = captureAuthoringTransactionSnapshot();
            const defaultModel = getDefaultStageModel(
                defaults.modelValues,
                undefined,
                defaults.modelCatalog,
            );
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
                prev.boundaryOutReferenceScale =
                    prevJoin.boundaryOutReferenceScale;
                prev.boundaryOutReferenceIncludeSoundtrack =
                    prevJoin.boundaryOutReferenceIncludeSoundtrack;
                prev.boundaryOutOverlap = prevJoin.boundaryOutOverlap;
            }
            clips.push(
                buildDefaultClip(
                    () => defaults,
                    (values) =>
                        getDefaultStageModel(
                            values,
                            undefined,
                            defaults.modelCatalog,
                        ),
                    false,
                    prev,
                ),
            );
            saveClips(clips, { origin: "timeline" });
            setSelection({
                kind: "clip",
                clipIdx: clips.length - 1,
                stageIdx: 0,
            });
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
        const transaction = captureAuthoringTransactionSnapshot();
        const catalogSnapshot = transaction.catalogStatus;
        if (
            renderBlockingArchitectureCatalogStatus(body, catalogSnapshot, () =>
                requestArchitectureCatalog(true),
            )
        ) {
            return;
        }
        // renderTimeline wipes the body's innerHTML, destroying the scroll
        // container. Preserve both axes across internal commits (add, delete,
        // drag, edits); otherwise a tall timeline snaps to its first row while
        // a wide one snaps left. External host-side replacements intentionally
        // reset because the old viewport is meaningless for new content.
        const previousScrollElement = scrollEl();
        const previousScroll =
            meta?.origin === "external"
                ? { left: 0, top: 0 }
                : {
                      left: previousScrollElement?.scrollLeft ?? 0,
                      top: previousScrollElement?.scrollTop ?? 0,
                  };
        try {
            const state = getState();
            const clips = state.clips;
            const globalPrompt = readGlobalPrompt();
            const architectureCatalog = transaction.defaults.modelCatalog;
            renderTimeline(body, clips, {
                fps: safeFps(state.fps),
                width: state.width,
                height: state.height,
                dimsExplicit: state.dimsExplicit,
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
                audioTracks: state.audioTracks,
                diagnostics: deriveAuthoringDiagnostics(clips, {
                    catalog: architectureCatalog,
                }),
                capabilities: transaction.capabilities,
            });
            renderRetainedArchitectureCatalogStatus(body, catalogSnapshot, () =>
                requestArchitectureCatalog(true),
            );
            viewport.restoreScroll(previousScroll);
            linking.reapplySelection(body, clips.length);
            detailStrip.render(meta, transaction);
            applySelectionHighlight(body);
        } catch (error) {
            console.warn("VideoStages: timeline render failed", error);
        }
    };

    const refresh = (): void => renderAll();

    const requestArchitectureCatalog = (forceRefresh = false): void => {
        const currentCatalog = getArchitectureCatalogSnapshot();
        if (
            !forceRefresh &&
            currentCatalog.catalog &&
            currentCatalog.status !== "refreshing"
        ) {
            renderAll();
            return;
        }
        // Repository subscribers own both request-start and settled paints.
        if (forceRefresh) {
            refreshAuthoritativeArchitectureCatalog();
        } else {
            loadAuthoritativeArchitectureCatalog();
        }
    };

    const init = (): void => {
        historyNeedsRebase = true;
        viewport.load();
        injectTimelineTab();
        const body = document.getElementById(TIMELINE_BODY_ID);
        if (body) {
            // Press-drag priority lives in the gesture router's table
            // (retake 50 > audio-span 40 > prompt-track 20 >
            // linking 10), not in attach order.
            retakeTrack.attach(body, gestures);
            audioSpanTrack.attach(body, gestures);
            linking.attach(body, gestures);
            promptTrack.attach(body, gestures);
            selectionTracks.attach(body);
            referencesTrack.attach(body, gestures);
            // Bind before the gesture router to preserve capture ordering, but
            // do not read/normalize document state until catalog authority is
            // ready. renderAll invokes the first detail render after success.
            detailStrip.attach(body, ensureDock(body), false);
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
        rebaseHistoryIfReady();
        hostLifecycle.bind();
        catalogUnsub?.();
        catalogUnsub = subscribeArchitectureCatalog((snapshot) => {
            if (snapshot.status === "ready" && snapshot.catalog) {
                getTimelineStore().invalidate();
                historyNeedsRebase = true;
                rebaseHistoryIfReady();
            }
            renderAll();
        });
        requestArchitectureCatalog();
    };

    const dispose = (): void => {
        catalogUnsub?.();
        catalogUnsub = null;
        hostLifecycle.dispose();
        retakeTrack.dispose();
        audioSpanTrack.dispose();
        linking.dispose();
        promptTrack.dispose();
        gestures.dispose();
        selectionTracks.dispose();
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
