import {
    AUDIO_SEGMENT_DEFAULT_LENGTH,
    AUDIO_SEGMENT_MIN_LENGTH,
    clamp,
    IC_LORA_STAGE_ALL,
    RETAKE_DEFAULT_DURATION,
    RETAKE_MIN_DURATION,
    RETAKE_STRENGTH_DEFAULT,
} from "./constants";
import { buildAudioBody } from "./detailStrip/audioPanel";
import { buildAudioSegmentBody } from "./detailStrip/audioSegmentPanel";
import { buildBoundaryBody } from "./detailStrip/boundaryPanel";
import { buildClipBody } from "./detailStrip/clipPanel";
import type {
    ClampedNumberOpts,
    DetailStripContext,
} from "./detailStrip/context";
import {
    buildPromptMajorBody,
    buildPromptMinorBody,
} from "./detailStrip/promptPanels";
import { buildRefBody } from "./detailStrip/refPanel";
import { buildSettingsBody } from "./detailStrip/settingsPanel";
import { buildNumber } from "./detailWidgets";
import {
    buildDefaultStage,
    reconcileIcLoraStage,
    removeRefAt,
} from "./normalization";
import { getClips, getState, saveClips, saveState } from "./persistence";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
import {
    getSelection,
    isSameSelection,
    setSelection,
    subscribeSelection,
} from "./selection";
import type { UpdateMeta } from "./store";
import { isVideoStagesEnabled, readStateToken } from "./swarmInputs";
import { stageChipLabel } from "./timelineDetail";
import { parseIntAttr } from "./trackDomUtils";
import type { AudioSegment, Clip, TimelineSelection } from "./types";
import { roundToTenth } from "./utils";

const STAGE_SELECTOR = "[data-vst-stage]";
const MODEL_SELECTOR = "[data-vst-model]";
const INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${MODEL_SELECTOR}`;

const DETAIL_CLASS = "vst-detail";
const DEBOUNCE_MS = 200;

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
    let boundBody: HTMLElement | null = null;
    let dockEl: HTMLElement | null = null;
    let unsubscribe: (() => void) | null = null;
    let sourceToken = "";
    let pendingTimer: ReturnType<typeof setTimeout> | null = null;
    let flushing = false;
    let rendering = false;
    let suppressSelectionRender = false;
    // Set on pointerdown over a range input, cleared on pointerup/pointercancel
    // (pointer can leave the dock mid-drag). Covers Safari-style browsers where
    // range inputs don't take focus on mousedown — see isSliderGesture.
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
    // behavior. Held edits flush on blur out of the dock, on any press outside
    // the dock (onDocPointerDown — track presses preventDefault, so blur alone
    // can't be relied on), on a number field's live `change` (spinner / Enter),
    // on Escape, on a selection change, before any structural op, and on
    // dispose.
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
    const buildClampedNumber = (opts: ClampedNumberOpts): HTMLInputElement => {
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

    // Structural REMOVE that stays in context: drop an entry, then reselect the
    // neighbour at the clamped index if any remain, else fall back. `remove`
    // returns the surviving count, or null to abort with nothing saved.
    const commitRemoval = (
        remove: (clips: Clip[]) => number | null,
        index: number,
        neighbour: (idx: number) => TimelineSelection,
        fallback: TimelineSelection,
    ): void =>
        structuralCommit((clips) => {
            const remaining = remove(clips);
            if (remaining === null) {
                return null;
            }
            return remaining > 0
                ? neighbour(Math.min(index, remaining - 1))
                : fallback;
        });

    const deleteRefEntry = (clipIdx: number, refIdx: number): void => {
        commitRemoval(
            (clips) => {
                const clip = clips[clipIdx];
                return clip && removeRefAt(clip, refIdx)
                    ? clip.refs.length
                    : null;
            },
            refIdx,
            (idx) => ({ kind: "ref", clipIdx, refIdx: idx }),
            { kind: "clip", clipIdx, stageIdx: 0 },
        );
    };

    const deleteWindowEntry = (clipIdx: number, windowIdx: number): void => {
        commitRemoval(
            (clips) => {
                const windows = clips[clipIdx]?.promptWindows;
                if (!windows || windowIdx < 0 || windowIdx >= windows.length) {
                    return null;
                }
                windows.splice(windowIdx, 1);
                return windows.length;
            },
            windowIdx,
            (idx) => ({ kind: "prompt-minor", clipIdx, windowIdx: idx }),
            { kind: "prompt-major", clipIdx },
        );
    };

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
        commitRemoval(
            (clips) => {
                const clip = clips[clipIdx];
                if (!clip?.audioSegments?.[segIdx]) {
                    return null;
                }
                clip.audioSegments = clip.audioSegments.filter(
                    (_, i) => i !== segIdx,
                );
                return clip.audioSegments.length;
            },
            segIdx,
            (idx) => ({ kind: "audio-segment", clipIdx, segIdx: idx }),
            { kind: "audio", clipIdx },
        );
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
                // Keep IC-LoRA stage targeting in the new index space: entries
                // on the deleted stage fall back to "all stages", later ones
                // shift down (a stale index would be silently skipped).
                for (const entry of clip.icLoras) {
                    if (entry.stage === stageIdx) {
                        entry.stage = IC_LORA_STAGE_ALL;
                    } else if (entry.stage > stageIdx) {
                        entry.stage -= 1;
                    }
                    reconcileIcLoraStage(entry, !!clip.sourceVideo);
                }
                return {
                    kind: "clip",
                    clipIdx,
                    stageIdx: clamp(stageIdx, 0, clip.stages.length - 1),
                };
            },
            { rebuildAfterSelect: true },
        );
    };

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
    //
    // A press OUTSIDE the dock flushes the held edit instead. Track gestures
    // preventDefault their mousedown, so a focused dock textarea never blurs and
    // the focusout flush never runs — yet the concluding click/drag may commit a
    // structural save (create/move/resize a window), which bumps the carrier
    // token and would make flushPending stale-drop the held edit. pointerdown
    // precedes every mousedown/click, so flushing here writes the edit first and
    // the gesture's own save lands on top of it.
    const onDocPointerDown = (event: Event): void => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }
        if (!dockEl?.contains(target)) {
            flushPending();
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

    const ensureDetail = (): HTMLElement => {
        if (!dockEl) {
            throw new Error("detail strip not attached");
        }
        return dockEl;
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

    // The commit pipeline, structural ops and closure-state accessors each
    // panel builder needs, passed explicitly instead of captured. `render`
    // (declared below) is forwarded lazily so it resolves at call time.
    const ctx: DetailStripContext = {
        commit,
        commitState,
        debouncedCommit,
        debouncedCommitState,
        buildClampedNumber,
        structuralCommit,
        render: () => render(),
        deleteRefEntry,
        deleteWindowEntry,
        createRetake,
        removeRetake,
        addAudioSegment,
        removeAudioSegment,
        addStage,
        deleteStage,
        selectStage,
        getBoundBody: () => boundBody,
        getDockEl: () => dockEl,
        getSettingsMode: () => settingsMode,
        setSettingsMode: (mode) => {
            settingsMode = mode;
        },
    };

    const buildBody = (sel: TimelineSelection, clips: Clip[]): HTMLElement => {
        switch (sel.kind) {
            case "clip":
                return buildClipBody(ctx, sel, clips);
            case "ref":
                return buildRefBody(ctx, sel, clips);
            case "audio":
                return buildAudioBody(ctx, sel, clips);
            case "audio-segment":
                return buildAudioSegmentBody(ctx, sel, clips);
            case "prompt-major":
                return buildPromptMajorBody(ctx, sel, clips);
            case "prompt-minor":
                return buildPromptMinorBody(ctx, sel, clips);
            case "retake":
                // Retake editing lives in the clip panel (like Stages): a
                // retake selection shows the owning clip's panel, whose Retake
                // section carries the same "retake-*" focus keys.
                return buildClipBody(
                    ctx,
                    { kind: "clip", clipIdx: sel.clipIdx, stageIdx: 0 },
                    clips,
                );
            case "boundary":
                return buildBoundaryBody(ctx, sel, clips);
            default:
                return buildSettingsBody(ctx);
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
