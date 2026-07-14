import { injectTimelineTab, TIMELINE_BODY_ID } from "./bottomTimelineTab";
import { buildDefaultClip } from "./normalization";
import { getClips, getState, saveClips } from "./persistence";
import {
    getDefaultStageModel,
    getRootDefaults,
    readInheritedDimsSignature,
} from "./rootDefaults";
import {
    getGroupToggle,
    getPromptInput,
    isVideoStagesEnabled,
    readCarrierSnapshot,
    readGlobalPrompt,
    readStateToken,
    restoreCarrierSnapshot,
    setVideoStagesEnabled,
} from "./swarmInputs";
import { createTimelineAudioSegmentTrack } from "./timelineAudioSegmentTrack";
import { createTimelineAudioTrack } from "./timelineAudioTrack";
import { createTimelineBoundaryTrack } from "./timelineBoundaryTrack";
import type { TimelineUnit } from "./timelineDetail";
import { createTimelineDetailStrip } from "./timelineDetailStrip";
import { createTimelineHistory } from "./timelineHistory";
import { createTimelineLinking } from "./timelineLinking";
import { createTimelinePlayhead } from "./timelinePlayhead";
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

const safeStateFps = (fps: number): number =>
    typeof fps === "number" && fps > 0 ? fps : 24;

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
    let lastSeenValue: string | null = null;
    let lastDimsSignature: string | null = null;
    let unit: TimelineUnit = "seconds";
    let pxPerSecond = DEFAULT_PX_PER_SECOND;
    let playheadSeconds = 0;
    let stripCollapsed = false;
    let selectionUnsub: (() => void) | null = null;
    const detailStrip = createTimelineDetailStrip({
        isCollapsed: () => stripCollapsed,
        setCollapsed: (collapsed) => {
            stripCollapsed = collapsed;
            saveViewState();
        },
        refresh: () => refresh(),
    });
    const linking = createTimelineLinking();
    const playhead = createTimelinePlayhead({
        setSeconds: (seconds) => {
            playheadSeconds = seconds;
            saveViewState();
            refresh();
        },
    });
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
                playheadSeconds?: unknown;
                stripCollapsed?: unknown;
            };
            if (typeof parsed.pxPerSecond === "number") {
                pxPerSecond = clampPxPerSecond(parsed.pxPerSecond);
            }
            if (parsed.unit === "frames" || parsed.unit === "seconds") {
                unit = parsed.unit;
            }
            if (
                typeof parsed.playheadSeconds === "number" &&
                Number.isFinite(parsed.playheadSeconds) &&
                parsed.playheadSeconds >= 0
            ) {
                playheadSeconds = parsed.playheadSeconds;
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
                    playheadSeconds,
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
        saveClips(clips);
    };

    const refresh = (): void => {
        const body = document.getElementById(TIMELINE_BODY_ID);
        if (!body) {
            return;
        }
        lastSeenValue = readStateToken();
        lastDimsSignature = readInheritedDimsSignature();
        history.capture();
        try {
            const state = getState();
            const clips = state.clips;
            renderTimeline(body, clips, {
                fps: safeStateFps(state.fps),
                width: state.width,
                height: state.height,
                dimsExplicit: state.dimsExplicit,
                fpsExplicit: state.fpsExplicit,
                unit,
                pxPerSecond,
                playheadSeconds,
                selectedIndex: linking.getSelectedIndex(),
                enabled: isVideoStagesEnabled(),
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
            linking.reapplySelection(body, clips.length);
            detailStrip.render();
            applySelectionHighlight(body);
        } catch (error) {
            console.warn("VideoStages: timeline render failed", error);
        }
    };

    const onInputChanged = (): void => {
        if (readStateToken() !== lastSeenValue) {
            refresh();
        }
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
        lastSeenValue = readStateToken();
        lastDimsSignature = readInheritedDimsSignature();
        inputSyncInterval = setInterval(() => {
            if (
                readStateToken() !== lastSeenValue ||
                readInheritedDimsSignature() !== lastDimsSignature
            ) {
                refresh();
            }
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
            // Attach before linking so retake-overlay gestures win the region's drag/click.
            retakeTrack.attach(body);
            // Attach before the audio track (and linking) so a segment gesture
            // wins over the audio-clip select and the region drag.
            audioSegmentTrack.attach(body);
            // Attach before linking so a ruler/hit-area press starts scrubbing
            // instead of a region drag.
            playhead.attach(body);
            linking.attach(body);
            promptTrack.attach(body);
            audioTrack.attach(body);
            boundaryTrack.attach(body);
            referencesTrack.attach(body);
            detailStrip.attach(body);
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
        playhead.dispose();
        linking.dispose();
        promptTrack.dispose();
        audioTrack.dispose();
        boundaryTrack.dispose();
        referencesTrack.dispose();
        detailStrip.dispose();
        selectionUnsub?.();
        selectionUnsub = null;
        const body = document.getElementById(TIMELINE_BODY_ID);
        body?.removeEventListener("click", onBodyClickSyncReadout);
        document.removeEventListener("keydown", onKeydown);
    };

    return { init, refresh, dispose };
};
