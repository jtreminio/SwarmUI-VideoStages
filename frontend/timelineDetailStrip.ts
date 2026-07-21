import {
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
    buildSegmentAudioSourceOptions,
    canUseClipLengthFromAudio,
    isAceStepFunAudioSource,
    resolveAudioSourceValue,
} from "./audioSource";
import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    AUDIO_SEGMENT_STEP,
    CLIP_DURATION_MAX,
    CLIP_DURATION_MIN,
    clamp,
    IC_LORA_ATTENTION_MAX,
    IC_LORA_ATTENTION_MIN,
    IC_LORA_ATTENTION_STEP,
    IC_LORA_AUTO,
    IC_LORA_SOURCE_UPLOAD,
    IC_LORA_STRENGTH_MAX,
    IC_LORA_STRENGTH_MIN,
    IC_LORA_STRENGTH_STEP,
    mediaPreviewSrc,
    PROMPT_WINDOW_MIN_DURATION,
    REF_FRAME_MIN,
    RETAKE_DEFAULT_DURATION,
    RETAKE_DURATION_STEP,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
    RETAKE_STRENGTH_MAX,
    RETAKE_STRENGTH_MIN,
    RETAKE_STRENGTH_STEP,
    ROOT_DIMENSION_MAX,
    ROOT_DIMENSION_MIN,
    ROOT_DIMENSION_STEP,
    ROOT_FPS_MAX,
    ROOT_FPS_MIN,
    STAGE_CONTROLNET_STRENGTH_MAX,
    STAGE_CONTROLNET_STRENGTH_MIN,
    STAGE_CONTROLNET_STRENGTH_STEP,
    STAGE_REF_STRENGTH_MAX,
    STAGE_REF_STRENGTH_MIN,
    STAGE_REF_STRENGTH_STEP,
} from "./constants";
import {
    buildCheckbox,
    buildField,
    buildInstanceRow,
    buildNumber,
    buildOptionSelect,
    buildSelect,
    buildSlider,
    buildTextarea,
    buildUploadRow,
    type OptionSpec,
    sectionLabel,
} from "./detailWidgets";
import {
    DIMENSION_PRESET_KEYS,
    matchPresetKey,
    presetBadgeElements,
    presetDimensions,
} from "./dimensionPresets";
import {
    clearIcLoraAutoFailure,
    ensureIcLoraAutoWeights,
    IC_LORA_AUTO_HINT_ATTR,
    icLoraAutoHint,
} from "./icLoraAutoDownload";
import {
    findIcLoraPreset,
    IC_LORA_PRESET_CUSTOM_ID,
    IC_LORA_PRESETS,
    icLoraTriggerHint,
} from "./icLoraPresets";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "./imageSource";
import {
    buildDefaultStage,
    defaultIcLora,
    getReferenceFrameMax,
    hasSlotSourcedIcLora,
    removeRefAt,
} from "./normalization";
import { getClips, getState, saveClips, saveState } from "./persistence";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import type { UpdateMeta } from "./store";
import { isVideoStagesEnabled, readStateToken } from "./swarmInputs";
import {
    type BoundaryPlan,
    crossfadePlanForClips,
} from "./timelineBoundaryTrack";
import {
    refSourceLabel,
    stageChipLabel,
    stageChipTitle,
} from "./timelineDetail";
import { applyClipDurationResize } from "./timelineEdit";
import {
    applyPromptWindowBegin,
    applyPromptWindowEnd,
    promptWindowNeighborBounds,
} from "./timelinePromptTrack";
import { BOUNDARY_GLYPH, BOUNDARY_LABEL } from "./timelineView";
import { parseIntAttr } from "./trackDomUtils";
import {
    type AudioSegment,
    type BoundaryOut,
    type Clip,
    type IcLora,
    type IcLoraControlType,
    REF_SOURCE_UPLOAD,
    type RootDefaults,
    type Stage,
    type TimelineSelection,
} from "./types";
import {
    getSelection,
    isSameSelection,
    setSelection,
    subscribeSelection,
} from "./uiState";
import { roundToTenth } from "./utils";

const STAGE_SELECTOR = "[data-vst-stage]";
const MODEL_SELECTOR = "[data-vst-model]";
const INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${MODEL_SELECTOR}`;

/** Clamp a (start, length) window inside [0, clipDur] with a minimum length. */
const clampStartLength = (
    start: number,
    length: number,
    clipDur: number,
    minLength: number,
): { start: number; length: number } => {
    const s = clamp(start, 0, Math.max(0, clipDur - minLength));
    const l = clamp(length, minLength, Math.max(minLength, clipDur - s));
    return { start: s, length: l };
};

const DETAIL_CLASS = "vst-detail";
// Stable, bounded group-section ids (never per-instance): collapse prefs persist
// across selections and cookies stay bounded. Instance identity rides the counter.
const GROUP_STAGES = "vstdock_stages";
const GROUP_ICLORA = "vstdock_iclora";
const GROUP_REF = "vstdock_ref";
const GROUP_AUDIO = "vstdock_audio";
const GROUP_AUDIOSEG = "vstdock_audioseg";
const GROUP_PROMPTMAJOR = "vstdock_promptmajor";
const GROUP_PROMPTMINOR = "vstdock_promptminor";
const GROUP_RETAKE = "vstdock_retake";
const GROUP_BOUNDARY = "vstdock_boundary";
const GROUP_SETTINGS = "vstdock_settings";
const DURATION_STEP = 0.1;
const UPSCALE_EPSILON = 1e-6;
const LORA_WEIGHT_STEP = 0.05;
const LORA_WEIGHT_DEFAULT = 1;
const DEBOUNCE_MS = 200;
const SETTINGS_INHERIT = "inherit";
const SETTINGS_CUSTOM = "custom";

export interface TimelineDetailStrip {
    /**
     * @param body listener-host: the tracks body carrying the capture-phase
     *   mousedown/click/keydown listeners for in-track stage chips.
     * @param dock render-host: the `.vst-detail` left-dock element (owned by the
     *   caller) that the strip renders its header + panel groups into.
     */
    attach(body: HTMLElement, dock: HTMLElement): void;
    /**
     * @param meta store-update metadata when this render is driven by a store
     *   notification (commit/external); omitted for direct renders (selection
     *   change, settings open, view-state repaints).
     */
    render(meta?: UpdateMeta): void;
    dispose(): void;
}

export interface TimelineDetailStripOptions {
    isCollapsed: () => boolean;
    setCollapsed: (collapsed: boolean) => void;
}

const clampDimension = (value: number): number =>
    clamp(
        Math.round(value) || ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MAX,
    );

const clampFps = (value: number): number =>
    clamp(Math.round(value) || ROOT_FPS_MIN, ROOT_FPS_MIN, ROOT_FPS_MAX);

const clampSelection = (
    sel: TimelineSelection,
    clips: Clip[],
): TimelineSelection => {
    if (sel.kind === "none") {
        return sel;
    }
    if (sel.kind === "boundary") {
        // A boundary is only valid between two adjacent clips, so the left clip must have a follower.
        return sel.leftClipIdx >= 0 && sel.leftClipIdx <= clips.length - 2
            ? sel
            : { kind: "none" };
    }
    if (sel.clipIdx < 0 || sel.clipIdx >= clips.length) {
        return { kind: "none" };
    }
    if (sel.kind === "clip") {
        const stageCount = clips[sel.clipIdx].stages.length;
        if (stageCount === 0) {
            return { kind: "none" };
        }
        const stageIdx = clamp(sel.stageIdx, 0, stageCount - 1);
        return stageIdx === sel.stageIdx
            ? sel
            : { kind: "clip", clipIdx: sel.clipIdx, stageIdx };
    }
    if (sel.kind === "ref") {
        return sel.refIdx >= 0 && sel.refIdx < clips[sel.clipIdx].refs.length
            ? sel
            : { kind: "none" };
    }
    if (sel.kind === "prompt-minor") {
        const windows = clips[sel.clipIdx].promptWindows ?? [];
        return sel.windowIdx >= 0 && sel.windowIdx < windows.length
            ? sel
            : { kind: "none" };
    }
    if (sel.kind === "retake") {
        return clips[sel.clipIdx].retake ? sel : { kind: "none" };
    }
    if (sel.kind === "audio-segment") {
        const segments = clips[sel.clipIdx].audioSegments ?? [];
        return sel.segIdx >= 0 && sel.segIdx < segments.length
            ? sel
            : { kind: "none" };
    }
    return sel;
};

export const createTimelineDetailStrip = (
    options: TimelineDetailStripOptions,
): TimelineDetailStrip => {
    // Listener-host (tracks body) vs render-host (the `.vst-detail` dock).
    let boundBody: HTMLElement | null = null;
    let dockEl: HTMLElement | null = null;
    let unsubscribe: (() => void) | null = null;
    let sourceToken = "";
    let pendingTimer: ReturnType<typeof setTimeout> | null = null;
    let flushing = false;
    let rendering = false;
    let suppressSelectionRender = false;
    // A range-slider drag is in progress. Set on pointerdown (capture) over a
    // range input inside the dock; cleared on pointerup / pointercancel anywhere
    // (the pointer can leave the dock mid-drag). While set, debounced edits are
    // HELD — the flush timer is never armed — so a mid-drag pause can't trigger a
    // save → timeline refresh → dock rebuild that destroys the range node the
    // browser's drag gesture is bound to. Complements the focus-based check in
    // isSliderGesture: this latch covers Safari-style browsers where a range
    // input does not take focus on mousedown, and drags whose focus sits
    // elsewhere.
    let sliderDragActive = false;
    // The resolution mode the user picked while editing timeline settings. Kept
    // across settings re-renders so an explicit "Custom" choice sticks even when
    // its dimensions coincide with a preset; reset when selection leaves "none".
    let settingsMode: string | null = null;
    let pendingFocus: {
        key: string;
        start: number | null;
        end: number | null;
    } | null = null;
    // The user deliberately moved focus OUT of the dock (tabbed to Generate, the
    // timeline, elsewhere). While set, focus preservation is disabled: a
    // flush/refresh/render that lands after the user left must NOT resurrect the
    // field they abandoned (focusout fires before the new target is active, so a
    // naive captureFocus would stash the departing field and the next render
    // would yank focus back). Cleared the moment focus re-enters the dock.
    let focusLeftDock = false;
    // The selection the dock is currently showing rendered widgets for. Lets a
    // same-panel selection move (e.g. relay W1→W2, ref R0→R1) do a targeted
    // highlight swap instead of a full innerHTML rebuild.
    let renderedSel: TimelineSelection | null = null;

    // Value-only commits: a dock-origin VALUE edit (a number/slider/checkbox/
    // select write that changes DATA but not panel STRUCTURE) already shows the
    // right value in its own field — only the TIMELINE needs repainting, never
    // the dock DOM. The value primitives (flushPending / commit / commitState)
    // save with `valueOnly: true`, which reaches render() as
    // meta.hint === "value-only" on the store's commit notification; render()
    // answers it with a light derived-UI sync instead of a rebuild. Renders with
    // no meta or without the hint — an external carrier change, undo/redo, a
    // selection change, a STRUCTURE-affecting dock commit — always rebuild.

    // A keyboard-edited dock field currently owns focus (textarea / typed text /
    // number). While one does, debounced edits are HELD — the timer is not armed,
    // so no save + timeline repaint fires mid-typing. Sliders (range), selects
    // and checkboxes are not keyboard fields, so they keep their live/debounced
    // behavior. Held edits flush on blur out of the dock, on a number field's
    // live `change` (spinner / Enter), on Escape, on a selection change, before
    // any structural op, and on dispose.
    const isTypingInDock = (): boolean => {
        if (!dockEl) {
            return false;
        }
        const active = document.activeElement;
        if (!(active instanceof HTMLElement) || !dockEl.contains(active)) {
            return false;
        }
        if (active instanceof HTMLTextAreaElement) {
            return true;
        }
        if (active instanceof HTMLInputElement) {
            return active.type === "text" || active.type === "number";
        }
        return false;
    };

    // A slider (range) drag is in progress, so debounced edits must be HELD
    // exactly like typing — the flush timer is never armed, so no save + timeline
    // repaint fires mid-gesture (which would rebuild the dock and destroy the
    // range node under the cursor). Two complementary signals:
    //   (a) the explicit pointer-gesture latch (covers Safari, where a range
    //       input doesn't focus on mousedown, and drags with focus elsewhere);
    //   (b) a range input, or its `.auto-slider-number` twin, currently owns
    //       focus inside the dock (covers Chrome/Firefox, where mousedown focuses
    //       the range).
    // Only the SAVE + REPAINT is deferred: the host's enableSliderForBox wiring
    // keeps syncing range → number (the live value readout) throughout the drag.
    const isSliderGesture = (): boolean => {
        if (sliderDragActive) {
            return true;
        }
        if (!dockEl) {
            return false;
        }
        const active = document.activeElement;
        if (!(active instanceof HTMLInputElement) || !dockEl.contains(active)) {
            return false;
        }
        return (
            active.type === "range" ||
            active.classList.contains("auto-slider-number")
        );
    };

    // --- focus preservation across the self-triggered refresh/rebuild ------

    const captureFocus = (): void => {
        // Focus has left the dock (or is about to, mid-focusout): there is no
        // live editing session to preserve, so never stash a field — that is the
        // exact value a subsequent render would wrongly restore to.
        if (focusLeftDock) {
            pendingFocus = null;
            return;
        }
        const active = document.activeElement;
        if (!(active instanceof HTMLElement) || !dockEl?.contains(active)) {
            pendingFocus = null;
            return;
        }
        const holder = active.closest("[data-vst-focus-key]");
        if (!holder || !dockEl.contains(holder)) {
            pendingFocus = null;
            return;
        }
        let start: number | null = null;
        let end: number | null = null;
        if (
            (active instanceof HTMLInputElement &&
                (active.type === "number" || active.type === "text")) ||
            active instanceof HTMLTextAreaElement
        ) {
            try {
                start = active.selectionStart;
                end = active.selectionEnd;
            } catch {}
        }
        pendingFocus = {
            key: holder.getAttribute("data-vst-focus-key") ?? "",
            start,
            end,
        };
    };

    const restoreFocus = (detail: HTMLElement): void => {
        const focus = pendingFocus;
        pendingFocus = null;
        if (!focus?.key) {
            return;
        }
        const holder = detail.querySelector<HTMLElement>(
            `[data-vst-focus-key="${focus.key}"]`,
        );
        if (!holder) {
            return;
        }
        holder.focus();
        if (
            (holder instanceof HTMLInputElement ||
                holder instanceof HTMLTextAreaElement) &&
            focus.start != null
        ) {
            try {
                holder.setSelectionRange(focus.start, focus.end ?? focus.start);
            } catch {}
        }
    };

    const tagFocus = (field: HTMLElement, key: string): HTMLElement => {
        const control =
            field.querySelector<HTMLElement>("input.auto-slider-number") ??
            field.querySelector<HTMLElement>("input, select") ??
            (field.matches("input, select") ? field : null);
        control?.setAttribute("data-vst-focus-key", key);
        return field;
    };

    // --- live-apply commit + coalesced debounce ---------------------------

    const isStale = (): boolean => readStateToken() !== sourceToken;

    type StateDraft = ReturnType<typeof getState>;
    interface PendingEntry {
        kind: "clips" | "state";
        mutate: ((clips: Clip[]) => void) | ((state: StateDraft) => void);
        // A contextually-clamped field's post-flush display corrector: re-derive
        // the value to SHOW from the freshly-saved clips (the same accessor the
        // panel builder read at build time) so a clamp the input's static
        // min/max couldn't express (relay neighbour bound, length capped by
        // start) is written back into the field. Keyed to the input by the
        // pending map key === the field's `data-vst-focus-key`. Set only by
        // buildClampedNumber, so it can never be forgotten for a new such field.
        readBack?: (clips: Clip[]) => number | null;
    }
    // Debounced edits keyed by field, so distinct fields never clobber each
    // other and a single flush applies them all in one write.
    const pending = new Map<string, PendingEntry>();

    /**
     * Apply every queued debounced edit in one batch. Runs on the debounce
     * timer AND synchronously before any render / staleness re-read, so a
     * committed-but-pending edit can never be silently dropped — the only
     * sanctioned drop is a stale carrier token.
     */
    const flushPending = (): void => {
        if (pendingTimer) {
            clearTimeout(pendingTimer);
            pendingTimer = null;
        }
        if (flushing || pending.size === 0) {
            return;
        }
        const entryList = [...pending.entries()];
        const entries = entryList.map(([, e]) => e);
        pending.clear();
        captureFocus();
        if (isStale()) {
            return;
        }
        const clipMutates = entries
            .filter((e) => e.kind === "clips")
            .map((e) => e.mutate as (clips: Clip[]) => void);
        const stateMutates = entries
            .filter((e) => e.kind === "state")
            .map((e) => e.mutate as (state: StateDraft) => void);
        // Every entry in the pending map is value-only by construction (the
        // debounced* commit helpers), so the whole flush is a value-only op:
        // the commit notification renders with the value-only hint, holding
        // the dock DOM stable while the timeline repaints.
        flushing = true;
        let flushedClips: Clip[] | null = null;
        try {
            if (clipMutates.length > 0) {
                const clips = getClips();
                for (const m of clipMutates) {
                    m(clips);
                }
                saveClips(clips, {
                    origin: "detail-strip",
                    valueOnly: true,
                });
                flushedClips = clips;
            }
            if (stateMutates.length > 0) {
                const state = getState();
                for (const m of stateMutates) {
                    m(state);
                }
                saveState(state, {
                    notifyDomChange: isVideoStagesEnabled(),
                    origin: "detail-strip",
                    valueOnly: true,
                });
            }
            sourceToken = readStateToken();
        } finally {
            flushing = false;
        }
        // Contextual-clamp write-back: a flushed field's mutator may have stored
        // a value the input's static min/max couldn't foresee (relay neighbour
        // bound, length capped by start). Re-derive each such field's display
        // from the freshly-saved clips and correct the input in place — no
        // rebuild. Runs after the value-only notify render, which never touches
        // inputs, so this is the sole corrector of the shown value.
        writeBackClamped(entryList, flushedClips);
        // Belt-and-suspenders: sync value-derived dock UI directly, in case no
        // notify render fired (e.g. a headless context with no prompt input).
        // Idempotent with the sync any value-only render already did.
        syncValueDerivedUI(renderedSel);
    };

    // Re-display contextually-clamped fields after a flush (see readBack). Only
    // rewrites when the stored display differs from what the input shows, so a
    // field whose typed value was already valid keeps its caret untouched. Safe
    // during a spinner/Enter commit on a focused field: showing the stored
    // clamped value is the correct behaviour (number inputs reset the caret on a
    // .value write, acceptable for a commit). Never runs mid-keystroke — a
    // keyboard field being typed holds its flush (schedulePending), so there is
    // no flush and thus no write-back until blur/change/Escape.
    const writeBackClamped = (
        entryList: [string, PendingEntry][],
        clips: Clip[] | null,
    ): void => {
        if (!dockEl || !clips) {
            return;
        }
        for (const [key, entry] of entryList) {
            if (!entry.readBack) {
                continue;
            }
            const input = dockEl.querySelector<HTMLInputElement>(
                `input[data-vst-focus-key="${key}"]`,
            );
            if (!input) {
                continue;
            }
            const display = entry.readBack(clips);
            if (display == null) {
                continue;
            }
            const next = `${display}`;
            if (input.value !== next) {
                input.value = next;
            }
        }
    };

    const schedulePending = (key: string, entry: PendingEntry): void => {
        // Synthetic `input` events dispatched by enableSlidersIn while the
        // strip is (re)rendering must never schedule a spurious write.
        if (rendering) {
            return;
        }
        pending.set(key, entry);
        if (pendingTimer) {
            clearTimeout(pendingTimer);
            pendingTimer = null;
        }
        // Hold (do not arm the flush timer) while a keyboard field is being
        // typed into or a range slider is being dragged, so the timeline never
        // repaints mid-gesture. The edit is safely queued and flushes on
        // blur-out / change / pointer release / selection change.
        if (isTypingInDock() || isSliderGesture()) {
            return;
        }
        pendingTimer = setTimeout(() => {
            pendingTimer = null;
            flushPending();
        }, DEBOUNCE_MS);
    };

    const debouncedCommit = (
        key: string,
        mutate: (clips: Clip[]) => void,
    ): void => {
        schedulePending(key, { kind: "clips", mutate });
    };

    const debouncedCommitState = (
        key: string,
        mutate: (state: StateDraft) => void,
    ): void => {
        schedulePending(key, { kind: "state", mutate });
    };

    // A number field whose commit mutator applies a CONTEXTUAL clamp the static
    // min/max/step can't express (a relay window's neighbour bound; a segment or
    // retake length capped by its start). A plain buildNumber only re-displays
    // its own attr-clamped value on commit (buildNumber's apply(true)), so it
    // would keep showing the raw typed value while the data holds the tighter
    // clamped one. buildClampedNumber closes that gap generically: it tags the
    // input with `key` as its focus-key and carries a `readBack` accessor — the
    // SAME accessor the panel reads at build time — into the pending entry, so
    // flushPending re-derives the display from the freshly-saved clips and writes
    // it back (see writeBackClamped). Because every contextually-clamped field is
    // built through here, `readBack` is a REQUIRED argument — a future such field
    // cannot forget the sync.
    const buildClampedNumber = (opts: {
        key: string;
        value: number;
        min: number;
        max: number;
        step: number;
        readBack: (clips: Clip[]) => number | null;
        mutate: (clips: Clip[], value: number) => void;
    }): HTMLInputElement => {
        const input = buildNumber(
            opts.value,
            opts.min,
            opts.max,
            opts.step,
            (value) => {
                schedulePending(opts.key, {
                    kind: "clips",
                    mutate: (clips: Clip[]) => opts.mutate(clips, value),
                    readBack: opts.readBack,
                });
            },
        );
        input.setAttribute("data-vst-focus-key", opts.key);
        return input;
    };

    const commit = (mutate: (clips: Clip[]) => void): void => {
        flushPending();
        captureFocus();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        mutate(clips);
        // commit() is used by both value-only edits (a select/checkbox that only
        // changes data — model, sampler, skip, …) and structure-affecting ones (a
        // source select that shows/hides rows). Value-only callers do NOT call
        // render() afterwards, so the commit notification's render must stay
        // value-only (the hint below); structure-affecting callers DO call
        // render() after this returns — a direct render with no meta, which
        // rebuilds.
        saveClips(clips, {
            origin: "detail-strip",
            valueOnly: true,
        });
        sourceToken = readStateToken();
        syncValueDerivedUI(renderedSel);
    };

    // Timeline settings ride in the top-level Data param, not in clips.
    const commitState = (mutate: (state: StateDraft) => void): void => {
        flushPending();
        captureFocus();
        if (isStale()) {
            render();
            return;
        }
        const state = getState();
        mutate(state);
        // Same value-vs-structure split as commit(): the commit notification's
        // render stays value-only; structure-affecting callers (resolution
        // mode, custom-FPS toggle) rebuild via their own render() call after
        // this returns.
        saveState(state, {
            notifyDomChange: isVideoStagesEnabled(),
            origin: "detail-strip",
            valueOnly: true,
        });
        sourceToken = readStateToken();
        syncValueDerivedUI(renderedSel);
    };

    /**
     * Shared skeleton of every STRUCTURAL edit (add/delete of refs, prompt
     * windows, retakes, audio segments, stages, LoRAs, IC-LoRAs): flush
     * pending value edits, bail to a rebuild when the carriers moved
     * underneath us, apply the mutation, save with the detail-strip origin,
     * refresh the stale-guard token, then run the follow-up. `apply` returns
     * the context-keeping selection to adopt, "render" for a plain rebuild,
     * or null when its guard failed (nothing is saved). `rebuildAfterSelect`
     * is the stage add/delete variant: swap the selection silently, then do
     * one full rebuild.
     */
    const structuralCommit = (
        apply: (clips: Clip[]) => TimelineSelection | "render" | null,
        opts?: { rebuildAfterSelect?: boolean },
    ): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const outcome = apply(clips);
        if (outcome === null) {
            return;
        }
        saveClips(clips, { origin: "detail-strip" });
        sourceToken = readStateToken();
        if (outcome === "render") {
            render();
            return;
        }
        if (opts?.rebuildAfterSelect) {
            suppressSelectionRender = true;
            setSelection(outcome);
            suppressSelectionRender = false;
            render();
            return;
        }
        setSelection(outcome);
    };

    // --- shared field builders live in detailWidgets.ts ------------------

    const deleteRefEntry = (clipIdx: number, refIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip || !removeRefAt(clip, refIdx)) {
                return null;
            }
            // Stay in context: the neighbouring ref if one remains, else the clip.
            return clip.refs.length > 0
                ? {
                      kind: "ref",
                      clipIdx,
                      refIdx: Math.min(refIdx, clip.refs.length - 1),
                  }
                : { kind: "clip", clipIdx, stageIdx: 0 };
        });
    };

    const deleteWindowEntry = (clipIdx: number, windowIdx: number): void => {
        structuralCommit((clips) => {
            const windows = clips[clipIdx]?.promptWindows;
            if (!windows || windowIdx < 0 || windowIdx >= windows.length) {
                return null;
            }
            windows.splice(windowIdx, 1);
            // Stay in context: the neighbouring window, else the major prompt.
            return windows.length > 0
                ? {
                      kind: "prompt-minor",
                      clipIdx,
                      windowIdx: Math.min(windowIdx, windows.length - 1),
                  }
                : { kind: "prompt-major", clipIdx };
        });
    };

    // --- structural retake operations ------------------------------------

    const createRetake = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip || clip.retake) {
                return null;
            }
            const clipDur = Math.max(0, clip.duration || 0);
            const lengthSeconds = Math.max(
                RETAKE_MIN_DURATION,
                Math.min(
                    RETAKE_DEFAULT_DURATION,
                    clipDur || RETAKE_DEFAULT_DURATION,
                ),
            );
            clip.retake = {
                startSeconds: 0,
                lengthSeconds,
                strength: RETAKE_STRENGTH_DEFAULT,
            };
            return { kind: "retake", clipIdx };
        });
    };

    const removeRetake = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip?.retake) {
                return null;
            }
            clip.retake = null;
            // Stay in context: back to the clip that owned the retake.
            return { kind: "clip", clipIdx, stageIdx: 0 };
        });
    };

    // --- structural audio-segment operations -----------------------------

    const addAudioSegment = (clipIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip) {
                return null;
            }
            // Each segment gets its own lane, so overlap is fine — the new
            // segment simply starts at 0 with the default length and is
            // APPENDED (the array index is the lane; lanes must not
            // reshuffle).
            const clipDur = Math.max(0, clip.duration || 0);
            if (clipDur < AUDIO_SEGMENT_MIN_LENGTH) {
                return null;
            }
            const segment: AudioSegment = {
                source: null,
                startSeconds: 0,
                trimStartSeconds: 0,
                lengthSeconds: roundToTenth(
                    Math.min(AUDIO_SEGMENT_DEFAULT_LENGTH, clipDur),
                ),
            };
            const segments = [...(clip.audioSegments ?? []), segment];
            clip.audioSegments = segments;
            return {
                kind: "audio-segment",
                clipIdx,
                segIdx: segments.length - 1,
            };
        });
    };

    const removeAudioSegment = (clipIdx: number, segIdx: number): void => {
        structuralCommit((clips) => {
            const clip = clips[clipIdx];
            if (!clip?.audioSegments?.[segIdx]) {
                return null;
            }
            clip.audioSegments = clip.audioSegments.filter(
                (_, i) => i !== segIdx,
            );
            // Stay in context: the neighbouring segment, else the audio panel.
            return clip.audioSegments.length > 0
                ? {
                      kind: "audio-segment",
                      clipIdx,
                      segIdx: Math.min(segIdx, clip.audioSegments.length - 1),
                  }
                : { kind: "audio", clipIdx };
        });
    };

    // --- structural stage operations -------------------------------------

    const selectStage = (clipIdx: number, stageIdx: number): void => {
        setSelection({ kind: "clip", clipIdx, stageIdx });
    };

    const addStage = (clipIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip) {
                    return null;
                }
                const last = clip.stages[clip.stages.length - 1] ?? null;
                clip.stages.push(
                    buildDefaultStage(
                        getRootDefaults,
                        getDefaultStageModel,
                        last,
                        clip.refs.length,
                    ),
                );
                return {
                    kind: "clip",
                    clipIdx,
                    stageIdx: clip.stages.length - 1,
                };
            },
            { rebuildAfterSelect: true },
        );
    };

    const deleteStage = (clipIdx: number, stageIdx: number): void => {
        structuralCommit(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip || clip.stages.length <= 1) {
                    return null;
                }
                if (stageIdx < 0 || stageIdx >= clip.stages.length) {
                    return null;
                }
                clip.stages.splice(stageIdx, 1);
                return {
                    kind: "clip",
                    clipIdx,
                    stageIdx: clamp(stageIdx, 0, clip.stages.length - 1),
                };
            },
            { rebuildAfterSelect: true },
        );
    };

    // --- region interaction (stage chips / model badge on the video track) --

    const handleActivation = (target: Element, shiftKey: boolean): void => {
        const stageChip = target.closest(STAGE_SELECTOR);
        if (stageChip instanceof HTMLElement) {
            const clipIdx = parseIntAttr(stageChip, "data-clip-idx");
            const stageIdx = parseIntAttr(stageChip, "data-stage-idx");
            if (clipIdx === null || stageIdx === null) {
                return;
            }
            if (shiftKey) {
                deleteStage(clipIdx, stageIdx);
            } else {
                selectStage(clipIdx, stageIdx);
            }
            return;
        }
        const modelBadge = target.closest(MODEL_SELECTOR);
        if (modelBadge instanceof HTMLElement) {
            const clipIdx = parseIntAttr(modelBadge, "data-clip-idx");
            if (clipIdx !== null) {
                selectStage(clipIdx, 0);
            }
        }
    };

    const onMouseDownCapture = (event: MouseEvent): void => {
        if (
            event.target instanceof Element &&
            event.target.closest(INTERACTIVE_SELECTOR)
        ) {
            event.stopPropagation();
        }
    };

    const onClickCapture = (event: MouseEvent): void => {
        if (
            !(event.target instanceof Element) ||
            !event.target.closest(INTERACTIVE_SELECTOR)
        ) {
            return;
        }
        event.stopPropagation();
        handleActivation(event.target, event.shiftKey);
    };

    const onKeyDownCapture = (event: KeyboardEvent): void => {
        if (event.key !== "Enter" && event.key !== " ") {
            return;
        }
        if (
            !(event.target instanceof Element) ||
            !event.target.closest(INTERACTIVE_SELECTOR)
        ) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        handleActivation(event.target, event.shiftKey);
    };

    // Escape inside the strip clears the selection (but never when a native
    // Swarm dropdown owns the key — it needs Escape to close itself).
    const onStripKeyDown = (event: KeyboardEvent): void => {
        if (event.key !== "Escape") {
            return;
        }
        if (
            event.target instanceof Element &&
            event.target.closest(".sui-popover")
        ) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        setSelection({ kind: "none" });
    };

    // Focus genuinely leaving the dock (into the timeline, the Generate button,
    // anywhere) flushes any held edit — so the carrier is written before any
    // downstream read (generation, refresh). Moving between two controls INSIDE
    // the dock keeps the edit held, which lets an in-panel re-point do a
    // targeted highlight swap without a disruptive rebuild.
    const onDockFocusOut = (event: FocusEvent): void => {
        // A full rebuild does `dockEl.innerHTML = ""`, which removes the focused
        // node and dispatches a synchronous focusout with a null relatedTarget.
        // That is OUR teardown, not the user leaving: captureFocus already stashed
        // the field and restoreFocus will re-focus it. Treating it as a genuine
        // blur here (setting focusLeftDock, nulling pendingFocus) is exactly the
        // bug that dropped the caret on Begin/End/Duration commits — so ignore any
        // focusout that fires mid-render.
        if (rendering) {
            return;
        }
        const next = event.relatedTarget;
        if (next instanceof Node && dockEl?.contains(next)) {
            // Focus moved to another control still INSIDE the dock: the editing
            // session continues there. Keep the edit held; the new field owns
            // focus and the next render captures IT, not the one just left.
            return;
        }
        // Focus genuinely left the dock. Mark the session ended BEFORE flushing
        // so the flush's captureFocus can't stash the departing field, then
        // flush the held edit so the carrier is written before any downstream
        // read (generation, refresh). restoreFocus stays disabled until focus
        // re-enters the dock, so a refresh-driven render can't yank focus back.
        focusLeftDock = true;
        pendingFocus = null;
        flushPending();
    };

    // Focus (re-)entering any dock control resumes an active editing session, so
    // focus preservation is armed again. Runs before onDockFocusOut clears it on
    // an in-dock hop, so an in-dock control-to-control move stays "inside".
    const onDockFocusIn = (): void => {
        focusLeftDock = false;
    };

    // A number field's `change` while it stays focused is a spinner click or
    // Enter — commit it live so those still feel immediate. A blur-driven change
    // (focus already moved on) is left to focusout so an in-dock field move
    // stays held.
    const onDockChange = (event: Event): void => {
        const target = event.target;
        if (
            target instanceof HTMLInputElement &&
            target.type === "number" &&
            document.activeElement === target
        ) {
            flushPending();
        }
    };

    // pointerdown on a range input inside the dock latches a slider drag, so the
    // whole gesture holds its debounced edit (see isSliderGesture). Capture-phase
    // so we see it before any per-widget handler. Document-level: the release
    // half must fire even after the pointer leaves the dock mid-drag.
    const onDocPointerDown = (event: Event): void => {
        const target = event.target;
        if (!(target instanceof Element) || !dockEl?.contains(target)) {
            return;
        }
        if (target.closest('input[type="range"]')) {
            sliderDragActive = true;
        }
    };

    // Pointer release ends the drag: clear the latch and, if the drag left an
    // edit queued, flush it once — a single save + repaint, replacing the
    // mid-drag debounce we suppressed. flushPending is idempotent when nothing is
    // pending, so a trailing native `change` (host wiring) can't double-write.
    const onDocPointerUp = (): void => {
        if (!sliderDragActive) {
            return;
        }
        sliderDragActive = false;
        flushPending();
    };

    // --- rendering --------------------------------------------------------

    const ensureDetail = (): HTMLElement => {
        if (!dockEl) {
            throw new Error("detail strip not attached");
        }
        return dockEl;
    };

    // --- host-convention input-group sections -----------------------------

    /**
     * A headerless host `.input-group` container. The dock breadcrumb is the
     * one label for the panel — section title rows only duplicated it — so a
     * group is now purely structural chrome (border, background, content
     * padding). `groupId` is a stable `vstdock_*` key kept as a CSS/test hook.
     */
    const buildGroup = (groupId: string, content: HTMLElement): HTMLElement => {
        const group = document.createElement("div");
        group.className = "input-group input-group-open";
        group.id = `auto-group-${groupId}`;

        const contentEl = document.createElement("div");
        contentEl.className = "input-group-content";
        contentEl.id = `input_group_content_${groupId}`;
        contentEl.appendChild(content);

        group.appendChild(contentEl);
        return group;
    };

    // Wrap a single-panel editor's field container in one host group inside a
    // `.vst-detail-body` scroll host.
    const wrapForm = (groupId: string, content: HTMLElement): HTMLElement => {
        const body = document.createElement("div");
        body.className = "vst-detail-body";
        body.appendChild(buildGroup(groupId, content));
        return body;
    };

    const breadcrumbFor = (sel: TimelineSelection): string => {
        switch (sel.kind) {
            case "clip":
                return `Clip ${sel.clipIdx} · ${stageChipLabel(sel.stageIdx)}`;
            case "ref":
                return `Ref ${sel.refIdx} · Clip ${sel.clipIdx}`;
            case "audio":
                return `Audio · Clip ${sel.clipIdx}`;
            case "audio-segment": {
                const seg =
                    getClips()[sel.clipIdx]?.audioSegments?.[sel.segIdx];
                if (!seg) {
                    return `Audio segment · Clip ${sel.clipIdx}`;
                }
                const start = roundToTenth(seg.startSeconds);
                const end = roundToTenth(seg.startSeconds + seg.lengthSeconds);
                return `Audio segment · Clip ${sel.clipIdx} · ${start}–${end} s`;
            }
            case "boundary":
                return `Boundary · Clip ${sel.leftClipIdx} → ${sel.leftClipIdx + 1}`;
            case "prompt-major":
                return `Prompt · Clip ${sel.clipIdx}`;
            case "prompt-minor": {
                const w =
                    getClips()[sel.clipIdx]?.promptWindows?.[sel.windowIdx];
                if (!w) {
                    return `Relay · Clip ${sel.clipIdx}`;
                }
                const start = roundToTenth(w.start);
                const end = roundToTenth(w.start + w.duration);
                return `Relay ${start}–${end}s · Clip ${sel.clipIdx}`;
            }
            case "retake": {
                const r = getClips()[sel.clipIdx]?.retake;
                if (!r) {
                    return `Retake · Clip ${sel.clipIdx}`;
                }
                const start = roundToTenth(r.startSeconds);
                const end = roundToTenth(r.startSeconds + r.lengthSeconds);
                return `Retake · Clip ${sel.clipIdx} · ${start}–${end} s`;
            }
            default:
                return "Timeline settings";
        }
    };

    const buildHeader = (
        sel: TimelineSelection,
        collapsed: boolean,
    ): HTMLElement => {
        const head = document.createElement("div");
        head.className = "vst-detail-head";
        const crumb = document.createElement("span");
        crumb.className = "vst-detail-crumb";
        crumb.textContent = breadcrumbFor(sel);
        // Clear the selection back to "none" (the Timeline Settings panel).
        const clear = document.createElement("button");
        clear.type = "button";
        clear.className = "basic-button small-button vst-detail-clear";
        clear.textContent = "Clear";
        clear.title = "Clear selection (show timeline settings)";
        clear.setAttribute("aria-label", clear.title);
        clear.hidden = sel.kind === "none";
        clear.addEventListener("click", () => {
            setSelection({ kind: "none" });
        });
        const toggle = document.createElement("button");
        toggle.type = "button";
        toggle.className = "basic-button small-button vst-detail-collapse";
        toggle.textContent = collapsed ? "▸" : "▾";
        toggle.title = collapsed
            ? "Expand detail strip"
            : "Collapse detail strip";
        toggle.setAttribute("aria-label", toggle.title);
        toggle.addEventListener("click", () => {
            options.setCollapsed(!options.isCollapsed());
            render();
        });
        head.append(crumb, clear, toggle);
        return head;
    };

    const buildClipColumn = (clip: Clip, clipIdx: number): HTMLElement => {
        const col = document.createElement("div");
        col.className = "vst-detail-col vst-detail-clip";

        const lengthDerived =
            clip.clipLengthFromAudio === true ||
            clip.clipLengthFromControlNet === true;
        const durationInput = buildNumber(
            clip.duration,
            CLIP_DURATION_MIN,
            CLIP_DURATION_MAX,
            DURATION_STEP,
            (value) => {
                debouncedCommit("duration", (clips) => {
                    const target = clips[clipIdx];
                    if (target && !lengthDerived) {
                        applyClipDurationResize(target, value, getRootDefaults);
                    }
                });
            },
        );
        durationInput.setAttribute("data-vst-focus-key", "duration");
        const durationField = buildField(
            "Duration (s)",
            durationInput,
            lengthDerived
                ? "(derived from audio/ControlNet source)"
                : undefined,
        );
        if (lengthDerived) {
            durationInput.disabled = true;
            durationField.classList.add("vst-field-disabled");
        }
        col.appendChild(durationField);

        col.appendChild(
            buildCheckbox("Skip this clip", clip.skipped === true, (value) => {
                commit((clips) => {
                    const target = clips[clipIdx];
                    if (target) {
                        target.skipped = value;
                    }
                });
            }),
        );

        return col;
    };

    const buildStageRail = (
        clip: Clip,
        clipIdx: number,
        stageIdx: number,
    ): HTMLElement => {
        const col = document.createElement("div");
        col.className = "vst-detail-col vst-detail-rail";

        // Stage tabs, left-aligned and taking the full remaining width.
        const list = document.createElement("div");
        list.className = "vst-detail-rail-list";
        clip.stages.forEach((stage, idx) => {
            const chip = document.createElement("button");
            chip.type = "button";
            chip.className = "vst-chip vst-stage-tab";
            if (idx === stageIdx) {
                chip.classList.add("vst-stage-tab-active");
            }
            if (stage.skipped) {
                chip.classList.add("vst-stage-tab-skipped");
            }
            chip.textContent = stageChipLabel(idx);
            chip.title = `${stageChipTitle(stage, idx)} · click to edit · Shift+click to delete`;
            chip.addEventListener("click", (event) => {
                if (event.shiftKey) {
                    deleteStage(clipIdx, idx);
                } else {
                    selectStage(clipIdx, idx);
                }
            });
            list.appendChild(chip);
        });
        col.appendChild(list);

        // Right-aligned action column: Add above Delete, both ALWAYS present;
        // Delete disables (never hides) when there is nothing to delete.
        const actions = document.createElement("div");
        actions.className = "vst-detail-rail-actions";
        const addBtn = document.createElement("button");
        addBtn.type = "button";
        addBtn.className =
            "basic-button small-button vst-detail-rail-btn vst-detail-add-stage";
        addBtn.textContent = "Add stage";
        addBtn.title = "Add a refine stage";
        addBtn.addEventListener("click", (event) => {
            event.preventDefault();
            addStage(clipIdx);
        });
        const deleteBtn = document.createElement("button");
        deleteBtn.type = "button";
        deleteBtn.className =
            "basic-button small-button vst-refs-delete vst-detail-rail-btn vst-detail-delete-stage";
        deleteBtn.textContent = "Delete stage";
        deleteBtn.disabled = clip.stages.length <= 1;
        deleteBtn.title = deleteBtn.disabled
            ? "A clip always keeps at least one stage"
            : `Delete stage ${stageChipLabel(stageIdx)}`;
        deleteBtn.addEventListener("click", (event) => {
            event.preventDefault();
            deleteStage(clipIdx, stageIdx);
        });
        actions.append(addBtn, deleteBtn);
        col.appendChild(actions);
        return col;
    };

    const buildParamsColumn = (
        clip: Clip,
        clipIdx: number,
        stageIdx: number,
        stage: Stage,
        defaults: RootDefaults,
    ): { col: HTMLElement; railSkipSync: (skipped: boolean) => void } => {
        const col = document.createElement("div");
        col.className = "vst-detail-col vst-detail-params";
        const isRefine = stageIdx >= 1;

        // Every field in this column edits the same stage: these two wrappers
        // carry the clips[clipIdx]?.stages[stageIdx] guard once.
        const stageCommit = (mutate: (target: Stage) => void): void => {
            commit((clips) => {
                const target = clips[clipIdx]?.stages[stageIdx];
                if (target) {
                    mutate(target);
                }
            });
        };
        const stageDebounced = (
            key: string,
            mutate: (target: Stage) => void,
        ): void => {
            debouncedCommit(key, (clips) => {
                const target = clips[clipIdx]?.stages[stageIdx];
                if (target) {
                    mutate(target);
                }
            });
        };
        const stageSlider = (
            label: string,
            focusKey: string,
            value: number,
            min: number,
            max: number,
            step: number,
            assign: (target: Stage, value: number) => void,
            opts?: { title?: string; onValue?: (value: number) => void },
        ): HTMLElement =>
            tagFocus(
                buildSlider(
                    label,
                    value,
                    min,
                    max,
                    step,
                    (v) => {
                        opts?.onValue?.(v);
                        stageDebounced(focusKey, (target) => assign(target, v));
                    },
                    opts?.title ? { title: opts.title } : undefined,
                ),
                focusKey,
            );
        const stageSelect = (
            values: string[],
            labels: string[],
            selected: string,
            assign: (target: Stage, value: string) => void,
        ): HTMLSelectElement =>
            buildSelect(values, labels, selected, (v) =>
                stageCommit((target) => assign(target, v)),
            );

        const fields = document.createElement("div");
        fields.className = "vst-detail-fields";
        const applyMute = (): void => {
            fields.classList.toggle(
                "vst-stage-fields-muted",
                stage.skipped === true,
            );
        };

        let railSkipSync: (skipped: boolean) => void = () => {};
        col.appendChild(
            buildCheckbox(
                "Skip this stage",
                stage.skipped === true,
                (value) => {
                    // Mutating the render snapshot before the commit is safe
                    // (the store deep-clones) but deliberate here only: the
                    // mute/rail sync reads it synchronously. Don't copy this
                    // pattern — go through stageCommit alone.
                    stage.skipped = value;
                    applyMute();
                    railSkipSync(value);
                    stageCommit((target) => {
                        target.skipped = value;
                    });
                },
            ),
        );
        col.appendChild(fields);
        applyMute();

        const modelField = buildField(
            "Model",
            stageSelect(
                defaults.modelValues,
                defaults.modelLabels,
                `${stage.model ?? ""}`,
                (target, value) => {
                    target.model = value;
                },
            ),
        );
        modelField.classList.add("vst-detail-span-2");
        fields.appendChild(modelField);

        fields.appendChild(
            stageSlider(
                "Steps",
                "steps",
                stage.steps,
                defaults.stepsMin,
                defaults.stepsMax,
                defaults.stepsStep,
                (target, value) => {
                    target.steps = Math.round(value);
                },
            ),
        );
        fields.appendChild(
            stageSlider(
                "CFG Scale",
                "cfg",
                stage.cfgScale,
                defaults.cfgScaleMin,
                defaults.cfgScaleMax,
                defaults.cfgScaleStep,
                (target, value) => {
                    target.cfgScale = value;
                },
            ),
        );

        if (isRefine) {
            fields.appendChild(
                stageSlider(
                    "Control",
                    "control",
                    stage.control,
                    defaults.controlMin,
                    defaults.controlMax,
                    defaults.controlStep,
                    (target, value) => {
                        target.control = value;
                    },
                    {
                        title: "Regen strength — higher = more of the stage is re-generated",
                    },
                ),
            );
            const methodSelect = stageSelect(
                defaults.upscaleMethodValues,
                defaults.upscaleMethodLabels,
                `${stage.upscaleMethod ?? ""}`,
                (target, value) => {
                    target.upscaleMethod = value;
                },
            );
            const methodField = buildField("Upscale Method", methodSelect);
            methodField.classList.add("vst-detail-span-2");
            const syncMethod = (upscale: number): void => {
                const disabled = Math.abs(upscale - 1) < UPSCALE_EPSILON;
                methodSelect.disabled = disabled;
                methodField.classList.toggle("vst-field-disabled", disabled);
                methodField.title = disabled
                    ? "Set Upscale above 1× to choose a method"
                    : "";
            };
            fields.appendChild(
                stageSlider(
                    "Upscale",
                    "upscale",
                    stage.upscale,
                    defaults.upscaleMin,
                    defaults.upscaleMax,
                    defaults.upscaleStep,
                    (target, value) => {
                        target.upscale = value;
                    },
                    { onValue: syncMethod },
                ),
            );
            fields.appendChild(methodField);
            syncMethod(stage.upscale);
        }

        fields.appendChild(
            buildField(
                "Sampler",
                stageSelect(
                    defaults.samplerValues,
                    defaults.samplerLabels,
                    `${stage.sampler ?? ""}`,
                    (target, value) => {
                        target.sampler = value;
                    },
                ),
            ),
        );
        fields.appendChild(
            buildField(
                "Scheduler",
                stageSelect(
                    defaults.schedulerValues,
                    defaults.schedulerLabels,
                    `${stage.scheduler ?? ""}`,
                    (target, value) => {
                        target.scheduler = value;
                    },
                ),
            ),
        );

        if (clip.refs.length > 0) {
            const refsHeader = document.createElement("div");
            refsHeader.className = "vst-detail-sec vst-detail-span-full";
            refsHeader.textContent = "Reference Strengths";
            fields.appendChild(refsHeader);
            // Hovering a strength row highlights its ref mark on the
            // references track, so R0/R1/… map visibly to the right image.
            const setRefHover = (refIdx: number, on: boolean): void => {
                boundBody
                    ?.querySelector<HTMLElement>(
                        `.vst-refs-mark[data-clip-idx="${clipIdx}"][data-ref-idx="${refIdx}"]`,
                    )
                    ?.classList.toggle("vst-ref-hover", on);
            };
            clip.refs.forEach((ref, refIdx) => {
                const current =
                    refIdx < stage.refStrengths.length
                        ? stage.refStrengths[refIdx]
                        : STAGE_REF_STRENGTH_MAX;
                const slider = buildSlider(
                    `R${refIdx}`,
                    current,
                    STAGE_REF_STRENGTH_MIN,
                    STAGE_REF_STRENGTH_MAX,
                    STAGE_REF_STRENGTH_STEP,
                    (value) => {
                        stageDebounced(`refstrength-${refIdx}`, (target) => {
                            if (refIdx < target.refStrengths.length) {
                                target.refStrengths[refIdx] = value;
                            }
                        });
                    },
                    {
                        title: `${refSourceLabel(ref.source ?? "")} · frame ${ref.frame ?? 0}${ref.fromEnd ? " (from end)" : ""}`,
                    },
                );
                slider.classList.add("vst-stage-ref-slider");
                tagFocus(slider, `ref-${refIdx}`);
                slider.addEventListener("mouseenter", () =>
                    setRefHover(refIdx, true),
                );
                slider.addEventListener("mouseleave", () =>
                    setRefHover(refIdx, false),
                );
                fields.appendChild(slider);
            });
        }

        // Guide Strength is the per-stage conditioning strength shared by every
        // IC-LoRA guide on the clip; only shown when the clip has IC-LoRAs.
        if (clip.icLoras.length > 0) {
            const controlNetSlider = buildSlider(
                "IC-LoRA Guide Strength",
                stage.controlNetStrength,
                STAGE_CONTROLNET_STRENGTH_MIN,
                STAGE_CONTROLNET_STRENGTH_MAX,
                STAGE_CONTROLNET_STRENGTH_STEP,
                (value) => {
                    stageDebounced("controlnet", (target) => {
                        target.controlNetStrength = value;
                    });
                },
                { hint: "Drive-video conditioning strength for this stage" },
            );
            tagFocus(controlNetSlider, "controlnet");
            fields.appendChild(controlNetSlider);
        }

        fields.appendChild(
            buildLorasSection(clipIdx, stageIdx, stage, defaults),
        );

        railSkipSync = (skipped: boolean): void => {
            const railChip = dockEl?.querySelector<HTMLElement>(
                `.vst-detail-rail-list .vst-stage-tab:nth-child(${stageIdx + 1})`,
            );
            railChip?.classList.toggle("vst-stage-tab-skipped", skipped);
        };

        return { col, railSkipSync };
    };

    const buildLorasSection = (
        clipIdx: number,
        stageIdx: number,
        stage: Stage,
        defaults: RootDefaults,
    ): HTMLElement => {
        const section = document.createElement("div");
        section.className =
            "vst-audio-field vst-stage-loras vst-detail-span-full";
        // Same section-header treatment as "Reference Strengths" above.
        const label = document.createElement("div");
        label.className = "vst-detail-sec";
        label.textContent = `LoRAs — Stage ${stageChipLabel(stageIdx)}`;
        section.appendChild(label);

        if (defaults.loraValues.length === 0) {
            const empty = document.createElement("small");
            empty.className = "vst-audio-field-hint";
            empty.textContent = "(no LoRAs available)";
            section.appendChild(empty);
        } else {
            const list = document.createElement("div");
            list.className = "vst-stage-lora-list";
            stage.loras.forEach((lora, index) => {
                const row = document.createElement("div");
                row.className = "vst-stage-lora-row";
                const select = buildSelect(
                    defaults.loraValues,
                    defaults.loraLabels,
                    lora.name,
                    (value) => {
                        commit((clips) => {
                            const target = clips[clipIdx]?.stages[stageIdx];
                            const entry = target?.loras[index];
                            if (entry) {
                                entry.name = value;
                            }
                        });
                    },
                );
                const weight = buildNumber(
                    lora.weight,
                    -10,
                    10,
                    LORA_WEIGHT_STEP,
                    (value) => {
                        debouncedCommit(`lora-${index}-weight`, (clips) => {
                            const target = clips[clipIdx]?.stages[stageIdx];
                            const entry = target?.loras[index];
                            if (entry) {
                                entry.weight = value;
                            }
                        });
                    },
                );
                weight.classList.add("vst-stage-lora-weight");
                weight.setAttribute(
                    "data-vst-focus-key",
                    `lora-${index}-weight`,
                );
                const remove = document.createElement("button");
                remove.type = "button";
                remove.className = "basic-button vst-stage-lora-remove";
                remove.textContent = "×";
                remove.title = "Remove this LoRA";
                remove.addEventListener("click", () => {
                    structuralCommit((clips) => {
                        const target = clips[clipIdx]?.stages[stageIdx];
                        if (!target) {
                            return null;
                        }
                        target.loras.splice(index, 1);
                        return "render";
                    });
                });
                row.append(select, weight, remove);
                list.appendChild(row);
            });
            section.appendChild(list);

            const addBtn = document.createElement("button");
            addBtn.type = "button";
            addBtn.className = "basic-button small-button vst-stage-lora-add";
            addBtn.textContent = "+ Add LoRA";
            addBtn.addEventListener("click", () => {
                structuralCommit((clips) => {
                    const target = clips[clipIdx]?.stages[stageIdx];
                    if (!target) {
                        return null;
                    }
                    target.loras.push({
                        name: defaults.loraValues[0] ?? "",
                        weight: LORA_WEIGHT_DEFAULT,
                    });
                    return "render";
                });
            });
            section.appendChild(addBtn);
        }

        return section;
    };

    // Retake section of the clip panel — lives alongside Stages, same shape:
    // a plain section label plus the fields (or an add button when none).
    const buildRetakeSection = (clip: Clip, clipIdx: number): HTMLElement => {
        const wrap = document.createElement("div");
        wrap.className = "vst-detail-stages-wrap";
        wrap.appendChild(sectionLabel("Retake"));
        const col = document.createElement("div");
        col.className = "vst-detail-col vst-detail-retake-col";
        wrap.appendChild(col);
        const retake = clip.retake;

        if (!retake) {
            const hint = document.createElement("small");
            hint.className = "vst-audio-field-hint";
            hint.textContent =
                "Regenerates a sub-range when refining a base video.";
            const addBtn = document.createElement("button");
            addBtn.type = "button";
            addBtn.className =
                "basic-button small-button vst-detail-rail-btn vst-detail-add-retake";
            addBtn.textContent = "Add retake";
            addBtn.addEventListener("click", (event) => {
                event.preventDefault();
                createRetake(clipIdx);
            });
            col.append(hint, addBtn);
            return wrap;
        }

        const clipDur = Math.max(RETAKE_MIN_DURATION, clip.duration || 0);
        const clampRetake = (
            start: number,
            length: number,
        ): { start: number; length: number } =>
            clampStartLength(start, length, clipDur, RETAKE_MIN_DURATION);

        const startInput = buildClampedNumber({
            key: "retake-start",
            value: retake.startSeconds,
            min: 0,
            max: Math.max(0, clipDur - RETAKE_MIN_DURATION),
            step: RETAKE_DURATION_STEP,
            readBack: (cs) => cs[clipIdx]?.retake?.startSeconds ?? null,
            mutate: (cs, value) => {
                const r = cs[clipIdx]?.retake;
                if (r) {
                    const next = clampRetake(value, r.lengthSeconds);
                    r.startSeconds = next.start;
                    r.lengthSeconds = next.length;
                }
            },
        });
        col.appendChild(buildField("Start (s)", startInput));

        const lengthInput = buildClampedNumber({
            key: "retake-length",
            value: retake.lengthSeconds,
            min: RETAKE_MIN_DURATION,
            max: clipDur,
            step: RETAKE_DURATION_STEP,
            readBack: (cs) => cs[clipIdx]?.retake?.lengthSeconds ?? null,
            mutate: (cs, value) => {
                const r = cs[clipIdx]?.retake;
                if (r) {
                    const next = clampRetake(r.startSeconds, value);
                    r.startSeconds = next.start;
                    r.lengthSeconds = next.length;
                }
            },
        });
        col.appendChild(buildField("Length (s)", lengthInput));

        col.appendChild(
            buildSlider(
                "Strength",
                retake.strength,
                RETAKE_STRENGTH_MIN,
                RETAKE_STRENGTH_MAX,
                RETAKE_STRENGTH_STEP,
                (value) => {
                    debouncedCommit("retake-strength", (cs) => {
                        const r = cs[clipIdx]?.retake;
                        if (r) {
                            r.strength = clamp(
                                value,
                                RETAKE_STRENGTH_MIN,
                                RETAKE_STRENGTH_MAX,
                            );
                        }
                    });
                },
            ),
        );

        const note = document.createElement("p");
        note.className = "vst-detail-note";
        note.textContent =
            "Applies when refining a base video; audio inside the window regenerates with the frames.";
        col.appendChild(note);

        const del = document.createElement("button");
        del.type = "button";
        del.className =
            "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-rail-btn";
        del.textContent = "Remove retake";
        del.addEventListener("click", (event) => {
            event.preventDefault();
            removeRetake(clipIdx);
        });
        col.appendChild(del);
        return wrap;
    };

    // IC-LoRAs section of the clip panel — same shape as Retake: a section
    // label plus stacked per-entry editors. Each entry pairs an in-context LoRA
    // with an optional uploaded drive video; presets seed strength/control-type
    // defaults and surface a trigger-phrase hint, nothing more.
    const buildIcLorasSection = (
        clip: Clip,
        clipIdx: number,
        defaults: RootDefaults,
    ): HTMLElement => {
        const wrap = document.createElement("div");
        wrap.className = "vst-detail-stages-wrap";
        wrap.appendChild(sectionLabel("IC-LoRAs"));
        const col = document.createElement("div");
        col.className = "vst-detail-col vst-detail-iclora-col";
        wrap.appendChild(col);

        const entryField = (
            clips: Clip[],
            entryIdx: number,
        ): IcLora | undefined => clips[clipIdx]?.icLoras[entryIdx];

        clip.icLoras.forEach((entry, entryIdx) => {
            const { row, fields } = buildInstanceRow({
                rowClass: "vst-detail-iclora",
                indexAttr: "data-vst-iclora-idx",
                index: entryIdx,
                active: false,
                title: `IC-LoRA ${entryIdx + 1}`,
                deleteLabel: "Remove",
                onDelete: () => {
                    structuralCommit((clips) => {
                        const target = clips[clipIdx];
                        if (!target || entryIdx >= target.icLoras.length) {
                            return null;
                        }
                        target.icLoras.splice(entryIdx, 1);
                        return "render";
                    });
                },
                repoint: () => {},
            });

            const presetSelect = buildOptionSelect(
                [
                    { value: IC_LORA_PRESET_CUSTOM_ID, label: "Custom" },
                    ...IC_LORA_PRESETS.map((preset) => ({
                        value: preset.id,
                        label: preset.displayName,
                    })),
                ],
                entry.preset,
                (value) => {
                    commit((clips) => {
                        const target = entryField(clips, entryIdx);
                        if (!target) {
                            return;
                        }
                        target.preset = value;
                        const preset = findIcLoraPreset(value);
                        if (preset) {
                            target.strength = preset.strength;
                            target.controlType = preset.controlType;
                        }
                    });
                    // A rejected [AUTO] download retries when its preset is re-picked.
                    clearIcLoraAutoFailure(value);
                    render();
                },
            );
            fields.appendChild(buildField("Preset", presetSelect));

            const loraSelect = buildSelect(
                [IC_LORA_AUTO, ...defaults.loraValues],
                [IC_LORA_AUTO, ...defaults.loraLabels],
                entry.lora,
                (value) => {
                    commit((clips) => {
                        const target = entryField(clips, entryIdx);
                        if (target) {
                            target.lora = value;
                        }
                    });
                    if (value === IC_LORA_AUTO) {
                        clearIcLoraAutoFailure(entry.preset);
                    }
                    render();
                },
            );
            fields.appendChild(buildField("LoRA", loraSelect));

            const strength = buildClampedNumber({
                key: `iclora-${entryIdx}-strength`,
                value: entry.strength,
                min: IC_LORA_STRENGTH_MIN,
                max: IC_LORA_STRENGTH_MAX,
                step: IC_LORA_STRENGTH_STEP,
                readBack: (cs) => entryField(cs, entryIdx)?.strength ?? null,
                mutate: (cs, value) => {
                    const target = entryField(cs, entryIdx);
                    if (target) {
                        target.strength = value;
                    }
                },
            });
            fields.appendChild(buildField("Strength", strength));

            const attention = buildClampedNumber({
                key: `iclora-${entryIdx}-attention`,
                value: entry.attentionStrength,
                min: IC_LORA_ATTENTION_MIN,
                max: IC_LORA_ATTENTION_MAX,
                step: IC_LORA_ATTENTION_STEP,
                readBack: (cs) =>
                    entryField(cs, entryIdx)?.attentionStrength ?? null,
                mutate: (cs, value) => {
                    const target = entryField(cs, entryIdx);
                    if (target) {
                        target.attentionStrength = value;
                    }
                },
            });
            fields.appendChild(buildField("Attention", attention));

            const controlSelect = buildOptionSelect(
                [
                    { value: "none", label: "None (raw video)" },
                    { value: "canny", label: "Canny edges" },
                    { value: "depth", label: "Depth map" },
                    { value: "normal", label: "Normal map" },
                ],
                entry.controlType,
                (value) => {
                    commit((clips) => {
                        const target = entryField(clips, entryIdx);
                        if (target) {
                            target.controlType = value as IcLoraControlType;
                        }
                    });
                },
            );
            fields.appendChild(buildField("Control", controlSelect));

            // Legacy slot-sourced entries keep their captured-branch source and
            // hide the upload row; everything authored here is upload-driven.
            if (entry.source === IC_LORA_SOURCE_UPLOAD) {
                fields.appendChild(
                    buildUploadRow(
                        "Drive Media",
                        "video/*,image/*",
                        entry.video?.fileName,
                        (data, fileName) => {
                            commit((clips) => {
                                const target = entryField(clips, entryIdx);
                                if (target) {
                                    target.video = { data, fileName };
                                }
                            });
                            render();
                        },
                        () => {
                            commit((clips) => {
                                const target = entryField(clips, entryIdx);
                                if (target) {
                                    target.video = null;
                                }
                            });
                            render();
                        },
                    ),
                );
            } else {
                const slot = document.createElement("small");
                slot.className = "vst-audio-field-hint";
                slot.textContent = `Driven by ${entry.source} (legacy source)`;
                fields.appendChild(slot);
            }

            const preset = findIcLoraPreset(entry.preset);
            const hintText = [
                preset?.note ?? "",
                icLoraTriggerHint(preset),
                !entry.video && entry.source === IC_LORA_SOURCE_UPLOAD
                    ? "No drive video: the LoRA still applies to the model (fine for HDR/text-driven use)."
                    : "",
            ]
                .filter(Boolean)
                .join(" ");
            if (hintText) {
                const hint = document.createElement("small");
                hint.className = "vst-audio-field-hint";
                hint.textContent = hintText;
                fields.appendChild(hint);
            }

            // [AUTO] fulfillment: start the preset-weights download when needed and surface its
            // state on a tagged line the downloader repaints in place as progress streams in.
            ensureIcLoraAutoWeights(entry, defaults.loraValues, render);
            const autoText = icLoraAutoHint(entry, defaults.loraValues);
            if (autoText) {
                const autoHint = document.createElement("small");
                autoHint.className = "vst-audio-field-hint";
                if (preset) {
                    autoHint.setAttribute(IC_LORA_AUTO_HINT_ATTR, preset.id);
                }
                autoHint.textContent = autoText;
                fields.appendChild(autoHint);
            }

            col.appendChild(row);
        });

        const addBtn = document.createElement("button");
        addBtn.type = "button";
        addBtn.className =
            "basic-button small-button vst-detail-rail-btn vst-detail-add-iclora";
        addBtn.textContent = "+ Add IC-LoRA";
        addBtn.addEventListener("click", (event) => {
            event.preventDefault();
            structuralCommit((clips) => {
                const target = clips[clipIdx];
                if (!target) {
                    return null;
                }
                target.icLoras.push(
                    defaultIcLora({
                        lora: defaults.loraValues[0] ?? IC_LORA_AUTO,
                    }),
                );
                return "render";
            });
        });
        col.appendChild(addBtn);
        return wrap;
    };

    const buildClipBody = (
        sel: Extract<TimelineSelection, { kind: "clip" }>,
        clips: Clip[],
    ): HTMLElement => {
        const body = document.createElement("div");
        body.className = "vst-detail-body vst-detail-clip-body";
        const clip = clips[sel.clipIdx];
        const stage = clip.stages[sel.stageIdx];
        const defaults = getRootDefaults();
        // The clip's own fields render bare (no group header/container — the
        // dock breadcrumb already names the clip), followed by a Stages group
        // (labelled rail + the selected stage's parameters) and a Retake group
        // — retake editing lives in the clip panel, like stages.
        body.appendChild(buildClipColumn(clip, sel.clipIdx));
        const params = buildParamsColumn(
            clip,
            sel.clipIdx,
            sel.stageIdx,
            stage,
            defaults,
        );
        const stagesWrap = document.createElement("div");
        stagesWrap.className = "vst-detail-stages-wrap";
        stagesWrap.append(
            sectionLabel("Stages"),
            buildStageRail(clip, sel.clipIdx, sel.stageIdx),
            params.col,
        );
        body.appendChild(buildGroup(GROUP_STAGES, stagesWrap));
        body.appendChild(
            buildGroup(
                GROUP_ICLORA,
                buildIcLorasSection(clip, sel.clipIdx, defaults),
            ),
        );
        body.appendChild(
            buildGroup(GROUP_RETAKE, buildRetakeSection(clip, sel.clipIdx)),
        );
        return body;
    };

    // --- reference editor -------------------------------------------------

    // The ref panel lists EVERY reference of the clip, stacked. The selected ref
    // is highlighted; touching any ref's control re-points the selection to it
    // (targeted highlight swap, no rebuild) and per-ref keys keep edits distinct.
    const buildRefBody = (
        sel: Extract<TimelineSelection, { kind: "ref" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx } = sel;
        const clip = clips[clipIdx];
        const body = document.createElement("div");
        body.className =
            "vst-detail-form-body vst-detail-instance-body vst-detail-ref-body";
        const frameMax = getReferenceFrameMax(getRootDefaults, clip);

        clip.refs.forEach((ref, refIdx) => {
            const options = buildImageSourceOptions(ref.source ?? "");
            const source = resolveImageSourceValue(ref.source ?? "", options);
            const isUpload = source === REF_SOURCE_UPLOAD;
            const { row, fields } = buildInstanceRow({
                rowClass: "vst-detail-ref-row",
                indexAttr: "data-vst-ref-index",
                index: refIdx,
                active: refIdx === sel.refIdx,
                title: `R${refIdx + 1}`,
                deleteLabel: "Delete",
                onDelete: () => deleteRefEntry(clipIdx, refIdx),
                repoint: () => setSelection({ kind: "ref", clipIdx, refIdx }),
            });

            const select = buildOptionSelect(options, source, (value) => {
                commit((cs) => {
                    const r = cs[clipIdx]?.refs[refIdx];
                    if (!r) {
                        return;
                    }
                    const resolved = resolveImageSourceValue(
                        value,
                        buildImageSourceOptions(value),
                    );
                    r.source = resolved;
                    if (resolved !== REF_SOURCE_UPLOAD) {
                        r.uploadedImage = null;
                        r.uploadFileName = null;
                    }
                });
                render();
            });
            fields.appendChild(buildField("Image Source", select));

            if (isUpload) {
                const preview = document.createElement("div");
                preview.className = "vst-refs-thumb-preview";
                const data = ref.uploadedImage?.data;
                if (data) {
                    preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
                    preview.classList.add("vst-refs-thumb-preview-set");
                }
                fields.appendChild(preview);
            }

            const frameInput = buildNumber(
                ref.frame,
                REF_FRAME_MIN,
                frameMax,
                1,
                (value) => {
                    debouncedCommit(`ref-${refIdx}-frame`, (cs) => {
                        const r = cs[clipIdx]?.refs[refIdx];
                        if (r) {
                            r.frame = clamp(
                                Math.round(value),
                                REF_FRAME_MIN,
                                frameMax,
                            );
                        }
                    });
                },
            );
            frameInput.setAttribute(
                "data-vst-focus-key",
                `ref-${refIdx}-frame`,
            );
            fields.appendChild(
                buildField(`Attach at Frame (1–${frameMax})`, frameInput),
            );

            fields.appendChild(
                buildCheckbox(
                    "Count from clip end",
                    ref.fromEnd === true,
                    (value) => {
                        commit((cs) => {
                            const r = cs[clipIdx]?.refs[refIdx];
                            if (r) {
                                r.fromEnd = value;
                            }
                        });
                    },
                ),
            );

            if (isUpload) {
                fields.appendChild(
                    buildUploadRow(
                        "Image Upload",
                        "image/*",
                        ref.uploadedImage?.fileName,
                        (data, fileName) => {
                            commit((cs) => {
                                const r = cs[clipIdx]?.refs[refIdx];
                                if (r) {
                                    r.uploadedImage = { data, fileName };
                                    r.uploadFileName = fileName;
                                }
                            });
                            render();
                        },
                        () => {
                            commit((cs) => {
                                const r = cs[clipIdx]?.refs[refIdx];
                                if (r) {
                                    r.uploadedImage = null;
                                    r.uploadFileName = null;
                                }
                            });
                            render();
                        },
                    ),
                );
            }
            body.appendChild(row);
        });

        return wrapForm(GROUP_REF, body);
    };

    // --- audio editor -----------------------------------------------------

    const buildAudioBody = (
        sel: Extract<TimelineSelection, { kind: "audio" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx } = sel;
        const clip = clips[clipIdx];
        const controlNetEnabled = hasSlotSourcedIcLora(clip.icLoras);
        const options = buildAudioSourceOptions(clip.audioSource ?? "", {
            controlNetEnabled,
        });
        const source = resolveAudioSourceValue(clip.audioSource ?? "", options);
        const canLength = canUseClipLengthFromAudio(source);
        const isAce = isAceStepFunAudioSource(source);

        const commitAudio = (mutate: (clip: Clip) => void): void => {
            commit((cs) => {
                const target = cs[clipIdx];
                if (!target) {
                    return;
                }
                mutate(target);
                const cnEnabled = hasSlotSourcedIcLora(target.icLoras);
                const nextSource = resolveAudioSourceValue(
                    target.audioSource,
                    buildAudioSourceOptions(target.audioSource, {
                        controlNetEnabled: cnEnabled,
                    }),
                );
                target.audioSource = nextSource;
                target.clipLengthFromAudio =
                    canUseClipLengthFromAudio(nextSource) &&
                    target.clipLengthFromAudio;
                if (target.clipLengthFromAudio) {
                    target.clipLengthFromControlNet = false;
                }
                target.saveAudioTrack =
                    isAceStepFunAudioSource(nextSource) &&
                    target.saveAudioTrack;
                target.uploadedAudio =
                    nextSource === AUDIO_SOURCE_UPLOAD
                        ? target.uploadedAudio
                        : null;
            });
        };

        const body = document.createElement("div");
        body.className = "vst-detail-form-body";

        const select = buildOptionSelect(
            options.map((o) => ({ value: o.value, label: o.label })),
            source,
            (value) => {
                commitAudio((c) => {
                    c.audioSource = value;
                });
                render();
            },
        );
        body.appendChild(buildField("Audio Source", select));

        body.appendChild(
            buildCheckbox("Reuse Audio", clip.reuseAudio === true, (value) => {
                commitAudio((c) => {
                    c.reuseAudio = value;
                });
            }),
        );

        const lengthRow = buildCheckbox(
            "Clip Length from Audio",
            clip.clipLengthFromAudio === true && canLength,
            (value) => {
                commitAudio((c) => {
                    c.clipLengthFromAudio = value;
                });
            },
            { disabled: !canLength },
        );
        body.appendChild(lengthRow);

        const saveRow = buildCheckbox(
            "Save Audio Track",
            clip.saveAudioTrack === true && isAce,
            (value) => {
                commitAudio((c) => {
                    c.saveAudioTrack = value;
                });
            },
            { disabled: !isAce },
        );
        body.appendChild(saveRow);

        if (source === AUDIO_SOURCE_UPLOAD) {
            body.appendChild(
                buildUploadRow(
                    "Audio Upload",
                    "audio/*",
                    clip.uploadedAudio?.fileName,
                    (data, fileName) => {
                        commitAudio((c) => {
                            c.uploadedAudio = { data, fileName };
                        });
                        render();
                    },
                    () => {
                        commitAudio((c) => {
                            c.uploadedAudio = null;
                        });
                        render();
                    },
                ),
            );
        }

        const segCount = clip.audioSegments?.length ?? 0;
        const addSegment = document.createElement("button");
        addSegment.type = "button";
        addSegment.className =
            "basic-button small-button vst-detail-add-segment";
        addSegment.textContent = "+ Add segment";
        addSegment.title =
            "Overlay an extra uploaded audio piece on this clip's audio lane";
        addSegment.addEventListener("click", (event) => {
            event.preventDefault();
            addAudioSegment(clipIdx);
        });
        body.appendChild(addSegment);
        if (segCount > 0) {
            const note = document.createElement("p");
            note.className = "vst-detail-note";
            note.textContent =
                segCount === 1
                    ? "1 overlay segment · mixed additively over the base audio."
                    : `${segCount} overlay segments · mixed additively over the base audio.`;
            body.appendChild(note);
        }
        return wrapForm(GROUP_AUDIO, body);
    };

    // --- audio segment editor ---------------------------------------------

    // The audio-segment panel lists EVERY overlay segment of the clip, stacked.
    // The selected segment is highlighted; touching any segment's control
    // re-points the selection to it (targeted swap, no rebuild) and per-segment
    // keys keep edits distinct.
    const buildAudioSegmentBody = (
        sel: Extract<TimelineSelection, { kind: "audio-segment" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx } = sel;
        const clip = clips[clipIdx];
        const segments = clip?.audioSegments ?? [];
        const body = document.createElement("div");
        body.className =
            "vst-detail-form-body vst-detail-instance-body vst-detail-seg-body";
        const clipDur = Math.max(AUDIO_SEGMENT_MIN_LENGTH, clip?.duration || 0);

        // Segments live on per-segment lanes and may overlap in time (the
        // backend mixes them additively) — start/length clamp only to the
        // clip's own bounds.
        const clampSegment = (
            start: number,
            length: number,
        ): { start: number; length: number } =>
            clampStartLength(start, length, clipDur, AUDIO_SEGMENT_MIN_LENGTH);

        segments.forEach((segment, segIdx) => {
            const { row, fields } = buildInstanceRow({
                rowClass: "vst-detail-seg-row",
                indexAttr: "data-vst-seg-index",
                index: segIdx,
                active: segIdx === sel.segIdx,
                title: `S${segIdx + 1}`,
                deleteLabel: "Remove segment",
                onDelete: () => removeAudioSegment(clipIdx, segIdx),
                repoint: () =>
                    setSelection({ kind: "audio-segment", clipIdx, segIdx }),
            });

            // Source select: an upload, or an AceStepFun generated track ref.
            const segSourceRef =
                typeof segment.source === "string" ? segment.source : "";
            const segSourceValue = segSourceRef || AUDIO_SOURCE_UPLOAD;
            const segSourceSelect = buildOptionSelect(
                buildSegmentAudioSourceOptions(segSourceRef),
                segSourceValue,
                (value) => {
                    commit((cs) => {
                        const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                        if (!seg) {
                            return;
                        }
                        if (isAceStepFunAudioSource(value)) {
                            seg.source = value;
                        } else if (typeof seg.source === "string") {
                            // Back to Upload: clear the ref so the upload
                            // picker prompts for a file.
                            seg.source = null;
                        }
                    });
                    render();
                },
            );
            fields.appendChild(buildField("Source", segSourceSelect));

            if (!segSourceRef) {
                fields.appendChild(
                    buildUploadRow(
                        "Audio Upload",
                        "audio/*",
                        typeof segment.source === "string"
                            ? undefined
                            : segment.source?.fileName,
                        (data, fileName) => {
                            commit((cs) => {
                                const seg =
                                    cs[clipIdx]?.audioSegments?.[segIdx];
                                if (seg) {
                                    seg.source = { data, fileName };
                                }
                            });
                            render();
                        },
                        () => {
                            commit((cs) => {
                                const seg =
                                    cs[clipIdx]?.audioSegments?.[segIdx];
                                if (seg) {
                                    seg.source = null;
                                }
                            });
                            render();
                        },
                    ),
                );
            }

            const startInput = buildClampedNumber({
                key: `seg-${segIdx}-start`,
                value: segment.startSeconds,
                min: 0,
                max: Math.max(0, clipDur - AUDIO_SEGMENT_MIN_LENGTH),
                step: AUDIO_SEGMENT_STEP,
                readBack: (cs) =>
                    cs[clipIdx]?.audioSegments?.[segIdx]?.startSeconds ?? null,
                mutate: (cs, value) => {
                    const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                    if (seg) {
                        const next = clampSegment(value, seg.lengthSeconds);
                        seg.startSeconds = next.start;
                        seg.lengthSeconds = next.length;
                    }
                },
            });
            fields.appendChild(buildField("Start (s)", startInput));

            const trimInput = buildNumber(
                segment.trimStartSeconds,
                0,
                CLIP_DURATION_MAX,
                AUDIO_SEGMENT_STEP,
                (value) => {
                    debouncedCommit(`seg-${segIdx}-trim`, (cs) => {
                        const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                        if (seg) {
                            seg.trimStartSeconds = Math.max(
                                0,
                                Math.round(value * 10) / 10,
                            );
                        }
                    });
                },
            );
            trimInput.setAttribute("data-vst-focus-key", `seg-${segIdx}-trim`);
            fields.appendChild(buildField("Trim start (s)", trimInput));

            const lengthInput = buildClampedNumber({
                key: `seg-${segIdx}-length`,
                value: segment.lengthSeconds,
                min: AUDIO_SEGMENT_MIN_LENGTH,
                max: clipDur,
                step: AUDIO_SEGMENT_STEP,
                readBack: (cs) =>
                    cs[clipIdx]?.audioSegments?.[segIdx]?.lengthSeconds ?? null,
                mutate: (cs, value) => {
                    const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                    if (seg) {
                        const next = clampSegment(seg.startSeconds, value);
                        seg.startSeconds = next.start;
                        seg.lengthSeconds = next.length;
                    }
                },
            });
            fields.appendChild(buildField("Length (s)", lengthInput));
            body.appendChild(row);
        });

        const note = document.createElement("p");
        note.className = "vst-detail-note";
        note.textContent =
            "Overlaid additively over the base audio; overlapping segments mix together.";
        body.appendChild(note);

        return wrapForm(GROUP_AUDIOSEG, body);
    };

    // --- prompt editors ---------------------------------------------------

    const buildPromptMajorBody = (
        sel: Extract<TimelineSelection, { kind: "prompt-major" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx } = sel;
        const body = document.createElement("div");
        body.className = "vst-detail-form-body vst-detail-prompt-body";
        body.appendChild(
            buildTextarea(
                clips[clipIdx].prompt ?? "",
                "Clip prompt (blank inherits the global prompt)…",
                "prompt-major",
                (value) => {
                    debouncedCommit("prompt-major", (cs) => {
                        const c = cs[clipIdx];
                        if (c) {
                            c.prompt = value.trim();
                        }
                    });
                },
            ),
        );
        return wrapForm(GROUP_PROMPTMAJOR, body);
    };

    // The relay panel lists EVERY minor prompt window of the clip, stacked. The
    // selected window is highlighted and (on a selection change) scrolled into
    // view and focused; focusing another window's textarea re-points the
    // selection so the timeline highlight follows without disrupting typing.
    const buildPromptMinorBody = (
        sel: Extract<TimelineSelection, { kind: "prompt-minor" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx, windowIdx } = sel;
        const clip = clips[clipIdx];
        const windows = clip?.promptWindows ?? [];
        const clipDur = Math.max(
            PROMPT_WINDOW_MIN_DURATION,
            clip?.duration || 0,
        );
        const body = document.createElement("div");
        body.className =
            "vst-detail-form-body vst-detail-prompt-body vst-detail-minor-body";

        windows.forEach((w, idx) => {
            const row = document.createElement("div");
            row.className = "vst-detail-minor-window";
            row.setAttribute("data-vst-minor-window", `${idx}`);
            if (idx === windowIdx) {
                row.classList.add("vst-detail-minor-active");
            }

            const head = document.createElement("div");
            head.className = "vst-detail-minor-head";
            const title = document.createElement("span");
            title.className = "vst-detail-minor-title";
            title.textContent = `W${idx + 1}`;
            const del = document.createElement("button");
            del.type = "button";
            del.className =
                "basic-button small-button vst-refs-delete vst-detail-delete vst-detail-minor-delete";
            del.textContent = "Delete";
            del.title = "Delete this prompt window";
            del.addEventListener("click", (event) => {
                event.preventDefault();
                deleteWindowEntry(clipIdx, idx);
            });
            head.append(title, del);
            row.appendChild(head);

            // Begin/end are editable, reusing the timeline's edge-resize clamp:
            // begin holds the end fixed, end holds the begin fixed, both stay in
            // the clip, keep a minimum duration, and can't cross a neighbouring
            // window (applyPromptWindowBegin/End). The inputs' min/max are the
            // SAME neighbour-aware intervals, so spinner arrows stop AT the
            // neighbour instead of running past and snapping back on commit.
            // Distinct pending keys per edge per window; the commit repaints the
            // on-track segment.
            const range = document.createElement("div");
            range.className = "vst-detail-minor-range";
            const bounds = clip ? promptWindowNeighborBounds(clip, idx) : null;

            // The browser's number spinner steps on a grid anchored at the
            // `min` attribute. A min of 0.25 puts whole-tenth values OFF the
            // 0.1 grid, so a down-spin snaps to x.95 — which roundSeconds
            // (half-up) rounds straight back to the original value: END could
            // never decrease. Keep the attrs ON the 0.1 grid (min rounded up,
            // max rounded down); the true 0.25-duration floor stays enforced
            // by the commit clamp (applyPromptWindowBegin/End).
            const gridCeil = (v: number): number => Math.ceil(v * 10) / 10;
            const gridFloor = (v: number): number => Math.floor(v * 10) / 10;

            const beginInput = buildClampedNumber({
                key: `minor-${idx}-begin`,
                value: roundToTenth(w.start),
                min: bounds?.beginMin ?? 0,
                max: gridFloor(
                    Math.max(0, clipDur - PROMPT_WINDOW_MIN_DURATION),
                ),
                step: 0.1,
                readBack: (cs) => {
                    const win = cs[clipIdx]?.promptWindows?.[idx];
                    return win ? roundToTenth(win.start) : null;
                },
                mutate: (cs, value) => {
                    const c = cs[clipIdx];
                    if (c) {
                        applyPromptWindowBegin(c, idx, value);
                    }
                },
            });
            range.appendChild(buildField("Begin (s)", beginInput));

            const endInput = buildClampedNumber({
                key: `minor-${idx}-end`,
                value: roundToTenth(w.start + w.duration),
                min: gridCeil(PROMPT_WINDOW_MIN_DURATION),
                max: bounds?.endMax ?? clipDur,
                step: 0.1,
                readBack: (cs) => {
                    const win = cs[clipIdx]?.promptWindows?.[idx];
                    return win ? roundToTenth(win.start + win.duration) : null;
                },
                mutate: (cs, value) => {
                    const c = cs[clipIdx];
                    if (c) {
                        applyPromptWindowEnd(c, idx, value);
                    }
                },
            });
            range.appendChild(buildField("End (s)", endInput));
            row.appendChild(range);

            const editor = buildTextarea(
                w.prompt ?? "",
                "Minor prompt for this window…",
                `minor-${idx}`,
                (value) => {
                    debouncedCommit(`minor-${idx}`, (cs) => {
                        const win = cs[clipIdx]?.promptWindows?.[idx];
                        if (win) {
                            win.prompt = value.trim();
                        }
                    });
                },
            );
            // Focusing this window's editor makes it the active selection so the
            // timeline highlight follows. setSelection no-ops on an identical
            // selection, so this is a no-op while typing in the already-selected
            // window and never interrupts the caret.
            editor.addEventListener("focus", () => {
                setSelection({ kind: "prompt-minor", clipIdx, windowIdx: idx });
            });
            row.appendChild(editor);
            body.appendChild(row);
        });

        return wrapForm(GROUP_PROMPTMINOR, body);
    };

    // --- boundary (seam) editor -------------------------------------------

    const formatOverlapSeconds = (frames: number, fps: number): string =>
        `${(frames / Math.max(1, fps)).toFixed(2)}s`;

    const buildBoundaryBody = (
        sel: Extract<TimelineSelection, { kind: "boundary" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { leftClipIdx } = sel;
        const body = document.createElement("div");
        body.className = "vst-detail-form-body vst-detail-boundary";
        const clip = clips[leftClipIdx];
        const value: BoundaryOut = clip?.boundaryOut ?? "cut";
        const state = getState();
        const fps = state.fps > 0 ? Math.round(state.fps) : 24;

        const joinSpecs: OptionSpec[] = (
            ["cut", "continue", "crossfade"] as BoundaryOut[]
        ).map((mode) => ({
            value: mode,
            label: `${BOUNDARY_LABEL[mode]} ${BOUNDARY_GLYPH[mode]}`,
        }));
        const select = buildOptionSelect(joinSpecs, value, (next) => {
            commit((cs) => {
                const c = cs[leftClipIdx];
                if (c) {
                    c.boundaryOut = (next as BoundaryOut) ?? "cut";
                }
            });
            render();
        });
        body.appendChild(
            buildField(
                `Join · Clip ${leftClipIdx} → ${leftClipIdx + 1}`,
                select,
            ),
        );

        const info = document.createElement("div");
        info.className = "vst-boundary-info";
        if (value === "cut") {
            info.textContent =
                "Hard cut — clips are concatenated with no overlap.";
        } else if (value === "continue") {
            info.textContent =
                `Continue — 1 frame (~${formatOverlapSeconds(1, fps)}) overlap. ` +
                "The next clip generates from this clip's final frame and the merge collapses the duplicated seam frame.";
        } else {
            const plan: BoundaryPlan = crossfadePlanForClips(clips, fps);
            const overlapFrames = plan.overlaps[leftClipIdx] ?? 0;
            if (plan.fallback || overlapFrames <= 0) {
                info.classList.add("vst-boundary-warn");
                info.textContent =
                    "This crossfade will fall back to a cut — a clip is too short for the overlap window.";
            } else {
                info.textContent =
                    `Crossfade — ${overlapFrames} frame${overlapFrames === 1 ? "" : "s"} ` +
                    `(~${formatOverlapSeconds(overlapFrames, fps)}) pixel dissolve.`;
            }
        }
        body.appendChild(info);

        if (value !== "cut") {
            const note = document.createElement("div");
            note.className = "vst-boundary-note";
            note.textContent =
                "Requires the LTX-2 model family — the backend degrades this boundary to a cut otherwise.";
            body.appendChild(note);
        }
        return wrapForm(GROUP_BOUNDARY, body);
    };

    // --- timeline settings (none selection) -------------------------------

    const buildSettingsBody = (): HTMLElement => {
        const state = getState();
        const defaults = getRootDefaults();
        const core = {
            width: defaults.width,
            height: defaults.height,
            fps: defaults.fps,
        };
        const defaultMode = !state.dimsExplicit
            ? SETTINGS_INHERIT
            : (matchPresetKey(state.width, state.height) ?? SETTINGS_CUSTOM);
        const mode = settingsMode ?? defaultMode;
        const isCustom = mode === SETTINGS_CUSTOM;
        const displayed =
            mode === SETTINGS_CUSTOM
                ? {
                      width: clampDimension(state.width),
                      height: clampDimension(state.height),
                  }
                : mode === SETTINGS_INHERIT
                  ? { width: core.width, height: core.height }
                  : (presetDimensions(mode) ?? {
                        width: clampDimension(state.width),
                        height: clampDimension(state.height),
                    });

        const body = document.createElement("div");
        body.className = "vst-detail-form-body vst-detail-settings";

        const resSpecs: OptionSpec[] = [
            {
                value: SETTINGS_INHERIT,
                label: `Use image resolution (${core.width}×${core.height})`,
            },
            ...DIMENSION_PRESET_KEYS.map((key) => ({
                value: key,
                label: key.replace("x", " × "),
            })),
            { value: SETTINGS_CUSTOM, label: "Custom" },
        ];
        const resSelect = buildOptionSelect(resSpecs, mode, (value) => {
            settingsMode = value;
            commitState((next) => {
                if (value === SETTINGS_INHERIT) {
                    next.dimsExplicit = false;
                } else if (value === SETTINGS_CUSTOM) {
                    next.dimsExplicit = true;
                    next.width = clampDimension(displayed.width);
                    next.height = clampDimension(displayed.height);
                } else {
                    const dims = presetDimensions(value);
                    if (dims) {
                        next.dimsExplicit = true;
                        next.width = dims.width;
                        next.height = dims.height;
                    }
                }
            });
            render();
        });
        body.appendChild(buildField("Resolution", resSelect));

        const widthInput = buildNumber(
            displayed.width,
            ROOT_DIMENSION_MIN,
            ROOT_DIMENSION_MAX,
            ROOT_DIMENSION_STEP,
            (value) => {
                debouncedCommitState("settings-width", (next) => {
                    next.dimsExplicit = true;
                    next.width = clampDimension(value);
                });
            },
        );
        widthInput.classList.add("vst-settings-num");
        widthInput.disabled = !isCustom;
        widthInput.setAttribute("data-vst-focus-key", "settings-width");

        const heightInput = buildNumber(
            displayed.height,
            ROOT_DIMENSION_MIN,
            ROOT_DIMENSION_MAX,
            ROOT_DIMENSION_STEP,
            (value) => {
                debouncedCommitState("settings-height", (next) => {
                    next.dimsExplicit = true;
                    next.height = clampDimension(value);
                });
            },
        );
        heightInput.classList.add("vst-settings-num");
        heightInput.disabled = !isCustom;
        heightInput.setAttribute("data-vst-focus-key", "settings-height");

        // Width and Height share one "Dimensions" row (W × H) to keep the
        // wrapping settings flow dense.
        const dimsPair = document.createElement("div");
        dimsPair.className = "vst-settings-dims";
        const dimsSep = document.createElement("span");
        dimsSep.className = "vst-settings-dims-sep";
        dimsSep.textContent = "×";
        dimsPair.append(widthInput, dimsSep, heightInput);
        const dimsField = buildField("Dimensions", dimsPair);
        if (!isCustom) {
            dimsField.classList.add("vst-audio-disabled");
        }
        body.appendChild(dimsField);

        const badges = document.createElement("div");
        badges.className = "vst-settings-badges";
        if (mode !== SETTINGS_CUSTOM && mode !== SETTINGS_INHERIT) {
            const els = presetBadgeElements(mode);
            if (els.length > 0) {
                badges.append(...els);
            }
        }
        badges.hidden = badges.childElementCount === 0;
        body.appendChild(badges);

        const fpsRow = buildCheckbox(
            "Custom FPS",
            state.fpsExplicit === true,
            (value) => {
                commitState((next) => {
                    next.fpsExplicit = value;
                    if (value) {
                        next.fps = clampFps(next.fps);
                    }
                });
                render();
            },
        );
        body.appendChild(fpsRow);

        const fpsInput = buildNumber(
            state.fpsExplicit ? clampFps(state.fps) : core.fps,
            ROOT_FPS_MIN,
            ROOT_FPS_MAX,
            1,
            (value) => {
                debouncedCommitState("settings-fps", (next) => {
                    next.fpsExplicit = true;
                    next.fps = clampFps(value);
                });
            },
        );
        fpsInput.classList.add("vst-settings-num");
        fpsInput.disabled = state.fpsExplicit !== true;
        fpsInput.setAttribute("data-vst-focus-key", "settings-fps");
        const fpsField = buildField("FPS", fpsInput);
        if (state.fpsExplicit !== true) {
            fpsField.classList.add("vst-audio-disabled");
        }
        body.appendChild(fpsField);
        return wrapForm(GROUP_SETTINGS, body);
    };

    const buildBody = (sel: TimelineSelection, clips: Clip[]): HTMLElement => {
        switch (sel.kind) {
            case "clip":
                return buildClipBody(sel, clips);
            case "ref":
                return buildRefBody(sel, clips);
            case "audio":
                return buildAudioBody(sel, clips);
            case "audio-segment":
                return buildAudioSegmentBody(sel, clips);
            case "prompt-major":
                return buildPromptMajorBody(sel, clips);
            case "prompt-minor":
                return buildPromptMinorBody(sel, clips);
            case "retake":
                // Retake editing lives in the clip panel (like Stages): a
                // retake selection shows the owning clip's panel, whose Retake
                // section carries the same "retake-*" focus keys.
                return buildClipBody(
                    { kind: "clip", clipIdx: sel.clipIdx, stageIdx: 0 },
                    clips,
                );
            case "boundary":
                return buildBoundaryBody(sel, clips);
            default:
                return buildSettingsBody();
        }
    };

    // The value-derived dock UI that a VALUE-ONLY edit can legitimately change
    // without any structural change to the panel. Kept in sync WITHOUT a rebuild
    // so the edited field's node (and its caret) survive.
    //
    // Audit of every panel builder's value-dependent, non-structural display:
    //   - `.vst-detail-crumb` breadcrumb: the ONLY text that a value edit moves —
    //     the relay time range (begin/end), audio-segment start/end, and retake
    //     start/end all feed breadcrumbFor(). Re-derived here from fresh clips.
    //   - Upscale-method disabled state: already synced LIVE in the Upscale
    //     slider's oninput (syncMethod), so no rebuild is needed for it.
    //   - Skip-stage rail chip styling + field mute: already synced LIVE in the
    //     Skip checkbox handler (applyMute + railSkipSync).
    //   - Group header counters (Clip N, "k of n", Stage k, relay W k/total,
    //     Clip N→M): all index/count based → only add/delete (STRUCTURAL) moves
    //     them, never a value edit, so nothing to sync here.
    //   - Settings badges / displayed dims, audio segment-count note, boundary
    //     info: only the STRUCTURAL selects (resolution mode, source, join type)
    //     change them, and those rebuild via their explicit render().
    const syncValueDerivedUI = (sel: TimelineSelection | null): void => {
        if (!dockEl || !sel) {
            return;
        }
        const crumb = dockEl.querySelector<HTMLElement>(".vst-detail-crumb");
        if (crumb) {
            crumb.textContent = breadcrumbFor(sel);
        }
    };

    const render = (meta?: UpdateMeta): void => {
        if (!dockEl) {
            return;
        }
        // Value-only commit from this dock (meta.hint, set by the value
        // primitives): the edited field already shows the new value. Never
        // rebuild (that would destroy the focused node); just re-sync the
        // derived UI. External changes, undo/redo, selection changes, and
        // structural dock commits arrive without the hint (or without meta at
        // all) and always rebuild. Guarded on an unchanged selection and an
        // expanded dock; anything else falls through to rebuild.
        if (
            meta?.origin === "detail-strip" &&
            meta.hint === "value-only" &&
            renderedSel &&
            !options.isCollapsed() &&
            isSameSelection(getSelection(), renderedSel)
        ) {
            sourceToken = readStateToken();
            syncValueDerivedUI(renderedSel);
            return;
        }
        // Persist any debounced edit before we tear down its widget, and before
        // enableSlidersIn's synthetic input events can schedule a new one.
        flushPending();
        rendering = true;
        try {
            sourceToken = readStateToken();
            const detail = ensureDetail();
            const clips = getClips();
            const raw = getSelection();
            const sel = clampSelection(raw, clips);
            if (!isSameSelection(raw, sel)) {
                setSelection(sel);
                return;
            }

            const collapsed = options.isCollapsed();
            // Preserve the dock scroll position across the innerHTML rebuild so a
            // value change never yanks the panel back to the top.
            const prevBody =
                detail.querySelector<HTMLElement>(".vst-detail-body");
            const savedScroll = prevBody ? prevBody.scrollTop : 0;
            // Capture whatever is focused in the dock RIGHT NOW, immediately
            // before we tear the DOM down. Every render is thus self-contained:
            // even a second render arriving back-to-back after an earlier one
            // already restored focus re-captures from the live DOM and restores
            // again, so the caret never escapes on the self-triggered refresh.
            captureFocus();
            detail.className = `${DETAIL_CLASS}${collapsed ? " vst-detail-collapsed" : ""}`;
            detail.innerHTML = "";
            detail.appendChild(buildHeader(sel, collapsed));
            if (!collapsed) {
                const body = buildBody(sel, clips);
                detail.appendChild(body);
                if (sel.kind === "clip" || sel.kind === "retake") {
                    enableSlidersIn(body);
                }
            }
            restoreFocus(detail);
            const newBody =
                detail.querySelector<HTMLElement>(".vst-detail-body");
            if (newBody && savedScroll > 0) {
                newBody.scrollTop = savedScroll;
            }
            if (!collapsed) {
                autoFocusSelection(detail, sel);
            }
            renderedSel = sel;
        } finally {
            rendering = false;
        }
    };

    // On a FRESH selection (arriving from outside the dock — a timeline click or
    // a newly created instance), bring the selected editor into view and focus it
    // ready to type. When focus is already inside the dock the user is
    // interacting in place, so we never steal it back or snap the caret.
    //
    // Applies to any single-text-control panel whose editor is the obvious caret
    // target: the relay window (`minor-N`) and the clip major prompt
    // (`prompt-major`). Multi-instance panels that are NOT a single prompt
    // (refs, audio segments) don't auto-focus — their controls are mixed and
    // stealing focus into one of them would be surprising.
    const focusKeyForSelection = (sel: TimelineSelection): string | null => {
        switch (sel.kind) {
            case "prompt-major":
                return "prompt-major";
            case "prompt-minor":
                return `minor-${sel.windowIdx}`;
            default:
                return null;
        }
    };

    const autoFocusSelection = (
        detail: HTMLElement,
        sel: TimelineSelection,
    ): void => {
        // The user deliberately moved focus out of the dock: a re-render (a
        // refresh, a value-change repaint) must not grab focus back into a
        // prompt they just left. Auto-focus is only for a FRESH selection
        // (onSelectionChanged re-arms it by clearing focusLeftDock).
        if (focusLeftDock) {
            return;
        }
        // Never steal focus when the selection change originated from inside the
        // dock (the user is editing in place).
        const active = document.activeElement;
        if (active instanceof HTMLElement && detail.contains(active)) {
            return;
        }
        const wantKey = focusKeyForSelection(sel);
        if (!wantKey) {
            return;
        }
        const editor = detail.querySelector<HTMLTextAreaElement>(
            `textarea[data-vst-focus-key="${wantKey}"]`,
        );
        if (!editor) {
            return;
        }
        editor.focus();
        const len = editor.value.length;
        try {
            editor.setSelectionRange(len, len);
        } catch {}
        if (typeof editor.scrollIntoView === "function") {
            editor.scrollIntoView({ block: "nearest" });
        }
    };

    // Multi-instance panels (relay windows, refs, audio segments) render EVERY
    // instance stacked, so moving the selection within the same panel/clip only
    // needs a highlight swap — no rebuild. This preserves scroll AND keeps the
    // caret at the click position (the known relay caret-to-end quirk).
    const targetedReselect = (sel: TimelineSelection): boolean => {
        if (!dockEl || !renderedSel || options.isCollapsed()) {
            return false;
        }
        const prev = renderedSel;
        if (prev.kind !== sel.kind) {
            return false;
        }
        const active = document.activeElement;
        const fromOutside = !(
            active instanceof HTMLElement && dockEl.contains(active)
        );
        const swap = (
            rowSelector: string,
            activeClass: string,
            index: number,
        ): boolean => {
            const rows = Array.from(
                dockEl?.querySelectorAll<HTMLElement>(rowSelector) ?? [],
            );
            if (index < 0 || index >= rows.length) {
                return false;
            }
            rows.forEach((row, i) => {
                row.classList.toggle(activeClass, i === index);
            });
            const crumb =
                dockEl?.querySelector<HTMLElement>(".vst-detail-crumb");
            if (crumb) {
                crumb.textContent = breadcrumbFor(sel);
            }
            if (
                fromOutside &&
                typeof rows[index].scrollIntoView === "function"
            ) {
                rows[index].scrollIntoView({ block: "nearest" });
            }
            renderedSel = sel;
            return true;
        };
        if (sel.kind === "prompt-minor" && prev.kind === "prompt-minor") {
            if (sel.clipIdx !== prev.clipIdx) {
                return false;
            }
            const ok = swap(
                ".vst-detail-minor-window",
                "vst-detail-minor-active",
                sel.windowIdx,
            );
            if (ok) {
                if (fromOutside) {
                    const editor = dockEl?.querySelector<HTMLTextAreaElement>(
                        `.vst-detail-minor-window[data-vst-minor-window="${sel.windowIdx}"] textarea`,
                    );
                    if (editor) {
                        editor.focus();
                        const len = editor.value.length;
                        try {
                            editor.setSelectionRange(len, len);
                        } catch {}
                    }
                }
            }
            return ok;
        }
        if (sel.kind === "ref" && prev.kind === "ref") {
            if (sel.clipIdx !== prev.clipIdx) {
                return false;
            }
            return swap(
                ".vst-detail-ref-row",
                "vst-detail-instance-active",
                sel.refIdx,
            );
        }
        if (sel.kind === "audio-segment" && prev.kind === "audio-segment") {
            if (sel.clipIdx !== prev.clipIdx) {
                return false;
            }
            return swap(
                ".vst-detail-seg-row",
                "vst-detail-instance-active",
                sel.segIdx,
            );
        }
        return false;
    };

    const onSelectionChanged = (sel: TimelineSelection): void => {
        if (suppressSelectionRender) {
            return;
        }
        // In-panel move within a stacked multi-instance list (relay W1→W2, ref
        // R0→R1, segment S0→S1 on the same clip): swap the highlight in place, no
        // rebuild — keeps scroll and the caret where the user clicked.
        if (targetedReselect(sel)) {
            return;
        }
        // A genuine selection change voids any focus we were preserving for the
        // previous panel, so a stale caret can't bleed into the new panel; the
        // new panel's own focus (live capture / auto-focus) takes over. A fresh
        // selection is a new editing session, so re-arm auto-focus/restore even
        // if the user had just left the dock.
        pendingFocus = null;
        focusLeftDock = false;
        // A fresh "none" re-derives the settings resolution mode from scratch.
        settingsMode = null;
        if (sel.kind !== "none" && options.isCollapsed()) {
            options.setCollapsed(false);
        }
        render();
    };

    const attach = (body: HTMLElement, dock: HTMLElement): void => {
        if (boundBody === body && dockEl === dock) {
            return;
        }
        dispose();
        boundBody = body;
        dockEl = dock;
        // Capture-phase in-track chip listeners stay on the tracks body; only the
        // render parent moves to the dock.
        body.addEventListener("mousedown", onMouseDownCapture, true);
        body.addEventListener("click", onClickCapture, true);
        body.addEventListener("keydown", onKeyDownCapture, true);
        // Escape-clears-selection lives on the dock (the render host).
        dock.addEventListener("keydown", onStripKeyDown);
        // Flush-on-blur-out and live-number-change flushing (see handlers).
        dock.addEventListener("focusout", onDockFocusOut);
        dock.addEventListener("focusin", onDockFocusIn);
        dock.addEventListener("change", onDockChange);
        // Slider-drag latch: pointerdown scoped to the dock, release listened for
        // document-wide (the pointer can leave the dock mid-drag).
        document.addEventListener("pointerdown", onDocPointerDown, true);
        document.addEventListener("pointerup", onDocPointerUp, true);
        document.addEventListener("pointercancel", onDocPointerUp, true);
        unsubscribe = subscribeSelection(onSelectionChanged);
        render();
    };

    const dispose = (): void => {
        flushPending();
        if (pendingTimer) {
            clearTimeout(pendingTimer);
            pendingTimer = null;
        }
        pending.clear();
        sliderDragActive = false;
        focusLeftDock = false;
        document.removeEventListener("pointerdown", onDocPointerDown, true);
        document.removeEventListener("pointerup", onDocPointerUp, true);
        document.removeEventListener("pointercancel", onDocPointerUp, true);
        if (unsubscribe) {
            unsubscribe();
            unsubscribe = null;
        }
        if (boundBody) {
            boundBody.removeEventListener(
                "mousedown",
                onMouseDownCapture,
                true,
            );
            boundBody.removeEventListener("click", onClickCapture, true);
            boundBody.removeEventListener("keydown", onKeyDownCapture, true);
            boundBody = null;
        }
        if (dockEl) {
            // The dock element is owned by the caller: drop our listeners and
            // clear our rendered content, but leave the element in place.
            dockEl.removeEventListener("keydown", onStripKeyDown);
            dockEl.removeEventListener("focusout", onDockFocusOut);
            dockEl.removeEventListener("focusin", onDockFocusIn);
            dockEl.removeEventListener("change", onDockChange);
            dockEl.className = DETAIL_CLASS;
            dockEl.innerHTML = "";
            dockEl = null;
        }
        renderedSel = null;
    };

    return { attach, render, dispose };
};
