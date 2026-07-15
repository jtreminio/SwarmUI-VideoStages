import {
    AUDIO_SOURCE_UPLOAD,
    buildAudioSourceOptions,
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
    mediaPreviewSrc,
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
    buildNumber,
    buildSelect,
    buildSlider,
} from "./detailWidgets";
import {
    DIMENSION_PRESET_KEYS,
    presetBadgeElements,
    presetDimensions,
} from "./dimensionPresets";
import {
    buildImageSourceOptions,
    resolveImageSourceValue,
} from "./imageSource";
import {
    buildDefaultStage,
    getReferenceFrameMax,
    removeRefAt,
} from "./normalization";
import { getClips, getState, saveClips, saveState } from "./persistence";
import { getDefaultStageModel, getRootDefaults } from "./rootDefaults";
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
import { BOUNDARY_GLYPH, BOUNDARY_LABEL } from "./timelineView";
import {
    type AudioSegment,
    type BoundaryOut,
    type Clip,
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

const STAGE_SELECTOR = "[data-vst-stage]";
const STAGE_ADD_SELECTOR = "[data-vst-stage-add]";
const MODEL_SELECTOR = "[data-vst-model]";
const INTERACTIVE_SELECTOR = `${STAGE_SELECTOR}, ${STAGE_ADD_SELECTOR}, ${MODEL_SELECTOR}`;

const DETAIL_CLASS = "vst-detail";
const DURATION_STEP = 0.1;
const UPSCALE_EPSILON = 1e-6;
const LORA_WEIGHT_STEP = 0.05;
const LORA_WEIGHT_DEFAULT = 1;
const DEBOUNCE_MS = 200;
const SETTINGS_INHERIT = "inherit";
const SETTINGS_CUSTOM = "custom";

export interface TimelineDetailStrip {
    attach(body: HTMLElement): void;
    render(): void;
    dispose(): void;
}

export interface TimelineDetailStripOptions {
    isCollapsed: () => boolean;
    setCollapsed: (collapsed: boolean) => void;
    /** Re-render the whole timeline (used after a settings/dims change). */
    refresh?: () => void;
}

const clampDimension = (value: number): number =>
    clamp(
        Math.round(value) || ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MIN,
        ROOT_DIMENSION_MAX,
    );

const clampFps = (value: number): number =>
    clamp(Math.round(value) || ROOT_FPS_MIN, ROOT_FPS_MIN, ROOT_FPS_MAX);

const roundSeconds = (seconds: number): number => Math.round(seconds * 10) / 10;

const parseIntAttr = (el: Element | null, name: string): number | null => {
    if (!el) {
        return null;
    }
    const raw = el.getAttribute(name);
    if (raw === null) {
        return null;
    }
    const value = Number.parseInt(raw, 10);
    return Number.isInteger(value) && value >= 0 ? value : null;
};

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
    let unsubscribe: (() => void) | null = null;
    let sourceToken = "";
    let pendingTimer: ReturnType<typeof setTimeout> | null = null;
    let flushing = false;
    let rendering = false;
    let suppressSelectionRender = false;
    // The resolution mode the user picked while editing timeline settings. Kept
    // across settings re-renders so an explicit "Custom" choice sticks even when
    // its dimensions coincide with a preset; reset when selection leaves "none".
    let settingsMode: string | null = null;
    let pendingFocus: {
        key: string;
        start: number | null;
        end: number | null;
    } | null = null;

    // --- focus preservation across the self-triggered refresh/rebuild ------

    const captureFocus = (): void => {
        const active = document.activeElement;
        if (!(active instanceof HTMLElement)) {
            return;
        }
        const holder = active.closest("[data-vst-focus-key]");
        if (!holder) {
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
        const entries = [...pending.values()];
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
        flushing = true;
        try {
            if (clipMutates.length > 0) {
                const clips = getClips();
                for (const m of clipMutates) {
                    m(clips);
                }
                saveClips(clips);
            }
            if (stateMutates.length > 0) {
                const state = getState();
                for (const m of stateMutates) {
                    m(state);
                }
                saveState(state, undefined, {
                    notifyDomChange: isVideoStagesEnabled(),
                });
            }
            sourceToken = readStateToken();
        } finally {
            flushing = false;
        }
        if (stateMutates.length > 0) {
            options.refresh?.();
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

    const commit = (mutate: (clips: Clip[]) => void): void => {
        flushPending();
        captureFocus();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        mutate(clips);
        saveClips(clips);
        sourceToken = readStateToken();
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
        saveState(state, undefined, {
            notifyDomChange: isVideoStagesEnabled(),
        });
        sourceToken = readStateToken();
        options.refresh?.();
    };

    // --- shared field builders for the non-clip editors ------------------

    interface OptionSpec {
        value: string;
        label: string;
        disabled?: boolean;
    }

    const buildOptionSelect = (
        specs: OptionSpec[],
        selected: string,
        onChange: (value: string) => void,
    ): HTMLSelectElement => {
        const select = document.createElement("select");
        select.className = "vst-audio-select";
        for (const spec of specs) {
            const opt = document.createElement("option");
            opt.value = spec.value;
            opt.textContent = spec.label;
            opt.dataset.cleanname = spec.label;
            opt.disabled = spec.disabled === true;
            opt.selected = spec.value === selected;
            select.appendChild(opt);
        }
        select.addEventListener("change", () => onChange(select.value));
        return select;
    };

    const buildTextarea = (
        value: string,
        placeholder: string,
        focusKey: string,
        onInput: (value: string) => void,
    ): HTMLTextAreaElement => {
        const editor = document.createElement("textarea");
        editor.className = "vst-prompt-editor vst-detail-prompt";
        editor.value = value;
        editor.placeholder = placeholder;
        editor.setAttribute("data-vst-focus-key", focusKey);
        editor.addEventListener("input", () => onInput(editor.value));
        if (typeof textPromptAddKeydownHandler === "function") {
            textPromptAddKeydownHandler(editor);
        }
        return editor;
    };

    const buildUploadRow = (
        label: string,
        accept: string,
        name: string | null | undefined,
        onFile: (data: string, fileName: string) => void,
        onClear: () => void,
    ): HTMLElement => {
        const row = document.createElement("div");
        row.className = "vst-audio-field vst-audio-upload";
        const uploadLabel = document.createElement("span");
        uploadLabel.className = "vst-audio-field-label";
        uploadLabel.textContent = label;
        const fileInput = document.createElement("input");
        fileInput.type = "file";
        fileInput.accept = accept;
        const fileName = document.createElement("span");
        fileName.className = "vst-audio-upload-name";
        fileName.textContent = name ? name : "No file chosen";
        const clearBtn = document.createElement("button");
        clearBtn.type = "button";
        clearBtn.className = "vst-audio-upload-clear";
        clearBtn.textContent = "Clear";
        clearBtn.hidden = !name;
        fileInput.addEventListener("change", () => {
            const file = fileInput.files?.[0];
            if (!file) {
                return;
            }
            const reader = new FileReader();
            reader.onload = () => {
                const data = `${reader.result ?? ""}`;
                if (data) {
                    onFile(data, file.name);
                }
            };
            reader.readAsDataURL(file);
        });
        clearBtn.addEventListener("click", () => onClear());
        row.append(uploadLabel, fileInput, fileName, clearBtn);
        return row;
    };

    const deleteRefEntry = (clipIdx: number, refIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip || !removeRefAt(clip, refIdx)) {
            return;
        }
        saveClips(clips);
        sourceToken = readStateToken();
        setSelection({ kind: "none" });
    };

    const deleteWindowEntry = (clipIdx: number, windowIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const windows = clips[clipIdx]?.promptWindows;
        if (!windows || windowIdx < 0 || windowIdx >= windows.length) {
            return;
        }
        windows.splice(windowIdx, 1);
        saveClips(clips);
        sourceToken = readStateToken();
        setSelection({ kind: "none" });
    };

    // --- structural retake operations ------------------------------------

    const createRetake = (clipIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip || clip.retake) {
            return;
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
        saveClips(clips);
        sourceToken = readStateToken();
        setSelection({ kind: "retake", clipIdx });
    };

    const removeRetake = (clipIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip?.retake) {
            return;
        }
        clip.retake = null;
        saveClips(clips);
        sourceToken = readStateToken();
        setSelection({ kind: "none" });
    };

    // --- structural audio-segment operations -----------------------------

    const addAudioSegment = (clipIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip) {
            return;
        }
        const clipDur = Math.max(0, clip.duration || 0);
        const lengthSeconds = Math.max(
            AUDIO_SEGMENT_MIN_LENGTH,
            Math.min(
                AUDIO_SEGMENT_DEFAULT_LENGTH,
                clipDur || AUDIO_SEGMENT_DEFAULT_LENGTH,
            ),
        );
        const segment: AudioSegment = {
            source: null,
            startSeconds: 0,
            trimStartSeconds: 0,
            lengthSeconds,
        };
        clip.audioSegments = [...(clip.audioSegments ?? []), segment];
        saveClips(clips);
        sourceToken = readStateToken();
        setSelection({
            kind: "audio-segment",
            clipIdx,
            segIdx: clip.audioSegments.length - 1,
        });
    };

    const removeAudioSegment = (clipIdx: number, segIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip?.audioSegments?.[segIdx]) {
            return;
        }
        clip.audioSegments = clip.audioSegments.filter((_, i) => i !== segIdx);
        saveClips(clips);
        sourceToken = readStateToken();
        setSelection({ kind: "none" });
    };

    // --- structural stage operations -------------------------------------

    const selectStage = (clipIdx: number, stageIdx: number): void => {
        setSelection({ kind: "clip", clipIdx, stageIdx });
    };

    const addStage = (clipIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip) {
            return;
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
        const newIdx = clip.stages.length - 1;
        saveClips(clips);
        sourceToken = readStateToken();
        suppressSelectionRender = true;
        setSelection({ kind: "clip", clipIdx, stageIdx: newIdx });
        suppressSelectionRender = false;
        render();
    };

    const deleteStage = (clipIdx: number, stageIdx: number): void => {
        flushPending();
        if (isStale()) {
            render();
            return;
        }
        const clips = getClips();
        const clip = clips[clipIdx];
        if (!clip || clip.stages.length <= 1) {
            return;
        }
        if (stageIdx < 0 || stageIdx >= clip.stages.length) {
            return;
        }
        clip.stages.splice(stageIdx, 1);
        saveClips(clips);
        sourceToken = readStateToken();
        const nextStage = clamp(stageIdx, 0, clip.stages.length - 1);
        suppressSelectionRender = true;
        setSelection({ kind: "clip", clipIdx, stageIdx: nextStage });
        suppressSelectionRender = false;
        render();
    };

    // --- region interaction (stage chips / model badge on the video track) --

    const handleActivation = (target: Element, shiftKey: boolean): void => {
        const addChip = target.closest(STAGE_ADD_SELECTOR);
        if (addChip instanceof HTMLElement) {
            const clipIdx = parseIntAttr(addChip, "data-clip-idx");
            if (clipIdx !== null) {
                addStage(clipIdx);
            }
            return;
        }
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

    // --- rendering --------------------------------------------------------

    const ensureDetail = (): HTMLElement => {
        const host = boundBody;
        if (!host) {
            throw new Error("detail strip not attached");
        }
        let detail = host.querySelector<HTMLElement>(
            `:scope > .${DETAIL_CLASS}`,
        );
        if (!detail) {
            detail = document.createElement("div");
            detail.className = DETAIL_CLASS;
            detail.addEventListener("keydown", onStripKeyDown);
        }
        if (detail.parentElement !== host || host.lastElementChild !== detail) {
            host.appendChild(detail);
        }
        return detail;
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
                const start = roundSeconds(seg.startSeconds);
                const end = roundSeconds(seg.startSeconds + seg.lengthSeconds);
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
                const start = roundSeconds(w.start);
                const end = roundSeconds(w.start + w.duration);
                return `Relay ${start}–${end}s · Clip ${sel.clipIdx}`;
            }
            case "retake": {
                const r = getClips()[sel.clipIdx]?.retake;
                if (!r) {
                    return `Retake · Clip ${sel.clipIdx}`;
                }
                const start = roundSeconds(r.startSeconds);
                const end = roundSeconds(r.startSeconds + r.lengthSeconds);
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
        const toggle = document.createElement("button");
        toggle.type = "button";
        toggle.className = "vst-detail-collapse";
        toggle.textContent = collapsed ? "▸" : "▾";
        toggle.title = collapsed
            ? "Expand detail strip"
            : "Collapse detail strip";
        toggle.setAttribute("aria-label", toggle.title);
        toggle.addEventListener("click", () => {
            options.setCollapsed(!options.isCollapsed());
            render();
        });
        head.append(crumb, toggle);
        return head;
    };

    const buildClipColumn = (clip: Clip, clipIdx: number): HTMLElement => {
        const col = document.createElement("div");
        col.className = "vst-detail-col vst-detail-clip";
        const label = document.createElement("p");
        label.className = "vst-detail-sec";
        label.textContent = `CLIP ${clipIdx}`;
        col.appendChild(label);

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

        if (!clip.retake) {
            const addRetake = document.createElement("button");
            addRetake.type = "button";
            addRetake.className = "vst-detail-add-retake";
            addRetake.textContent = "+ Retake";
            addRetake.title =
                "Add a retake window (regenerates a sub-range when refining a base video)";
            addRetake.addEventListener("click", (event) => {
                event.preventDefault();
                createRetake(clipIdx);
            });
            col.appendChild(addRetake);
        }
        return col;
    };

    const buildStageRail = (
        clip: Clip,
        clipIdx: number,
        stageIdx: number,
    ): HTMLElement => {
        const col = document.createElement("div");
        col.className = "vst-detail-col vst-detail-rail";
        const label = document.createElement("p");
        label.className = "vst-detail-sec";
        label.textContent = "STAGES";
        col.appendChild(label);

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
        const addChip = document.createElement("button");
        addChip.type = "button";
        addChip.className = "vst-chip vst-stage-tab vst-stage-tab-add";
        addChip.textContent = "+";
        addChip.title = "Add a refine stage";
        addChip.addEventListener("click", () => addStage(clipIdx));
        list.appendChild(addChip);
        col.appendChild(list);

        if (clip.stages.length > 1) {
            const deleteBtn = document.createElement("button");
            deleteBtn.type = "button";
            deleteBtn.className = "vst-refs-delete vst-detail-delete-stage";
            deleteBtn.textContent = "Delete stage";
            deleteBtn.addEventListener("click", (event) => {
                event.preventDefault();
                deleteStage(clipIdx, stageIdx);
            });
            col.appendChild(deleteBtn);
        }
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
                    stage.skipped = value;
                    applyMute();
                    railSkipSync(value);
                    commit((clips) => {
                        const target = clips[clipIdx]?.stages[stageIdx];
                        if (target) {
                            target.skipped = value;
                        }
                    });
                },
            ),
        );
        col.appendChild(fields);
        applyMute();

        const modelField = buildField(
            "Model",
            buildSelect(
                defaults.modelValues,
                defaults.modelLabels,
                `${stage.model ?? ""}`,
                (value) => {
                    commit((clips) => {
                        const target = clips[clipIdx]?.stages[stageIdx];
                        if (target) {
                            target.model = value;
                        }
                    });
                },
            ),
        );
        modelField.classList.add("vst-detail-span-2");
        fields.appendChild(modelField);

        fields.appendChild(
            tagFocus(
                buildSlider(
                    "Steps",
                    stage.steps,
                    defaults.stepsMin,
                    defaults.stepsMax,
                    defaults.stepsStep,
                    (value) => {
                        debouncedCommit("steps", (clips) => {
                            const target = clips[clipIdx]?.stages[stageIdx];
                            if (target) {
                                target.steps = Math.round(value);
                            }
                        });
                    },
                ),
                "steps",
            ),
        );
        fields.appendChild(
            tagFocus(
                buildSlider(
                    "CFG Scale",
                    stage.cfgScale,
                    defaults.cfgScaleMin,
                    defaults.cfgScaleMax,
                    defaults.cfgScaleStep,
                    (value) => {
                        debouncedCommit("cfg", (clips) => {
                            const target = clips[clipIdx]?.stages[stageIdx];
                            if (target) {
                                target.cfgScale = value;
                            }
                        });
                    },
                ),
                "cfg",
            ),
        );

        if (isRefine) {
            fields.appendChild(
                tagFocus(
                    buildSlider(
                        "Control",
                        stage.control,
                        defaults.controlMin,
                        defaults.controlMax,
                        defaults.controlStep,
                        (value) => {
                            debouncedCommit("control", (clips) => {
                                const target = clips[clipIdx]?.stages[stageIdx];
                                if (target) {
                                    target.control = value;
                                }
                            });
                        },
                        {
                            title: "Regen strength — higher = more of the stage is re-generated",
                        },
                    ),
                    "control",
                ),
            );
            const methodSelect = buildSelect(
                defaults.upscaleMethodValues,
                defaults.upscaleMethodLabels,
                `${stage.upscaleMethod ?? ""}`,
                (value) => {
                    commit((clips) => {
                        const target = clips[clipIdx]?.stages[stageIdx];
                        if (target) {
                            target.upscaleMethod = value;
                        }
                    });
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
                tagFocus(
                    buildSlider(
                        "Upscale",
                        stage.upscale,
                        defaults.upscaleMin,
                        defaults.upscaleMax,
                        defaults.upscaleStep,
                        (value) => {
                            syncMethod(value);
                            debouncedCommit("upscale", (clips) => {
                                const target = clips[clipIdx]?.stages[stageIdx];
                                if (target) {
                                    target.upscale = value;
                                }
                            });
                        },
                    ),
                    "upscale",
                ),
            );
            fields.appendChild(methodField);
            syncMethod(stage.upscale);
        }

        fields.appendChild(
            buildField(
                "Sampler",
                buildSelect(
                    defaults.samplerValues,
                    defaults.samplerLabels,
                    `${stage.sampler ?? ""}`,
                    (value) => {
                        commit((clips) => {
                            const target = clips[clipIdx]?.stages[stageIdx];
                            if (target) {
                                target.sampler = value;
                            }
                        });
                    },
                ),
            ),
        );
        fields.appendChild(
            buildField(
                "Scheduler",
                buildSelect(
                    defaults.schedulerValues,
                    defaults.schedulerLabels,
                    `${stage.scheduler ?? ""}`,
                    (value) => {
                        commit((clips) => {
                            const target = clips[clipIdx]?.stages[stageIdx];
                            if (target) {
                                target.scheduler = value;
                            }
                        });
                    },
                ),
            ),
        );

        if (clip.refs.length > 0) {
            const refsHeader = document.createElement("div");
            refsHeader.className = "vst-detail-sec vst-detail-span-full";
            refsHeader.textContent = "Reference Strengths";
            fields.appendChild(refsHeader);
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
                        debouncedCommit(`refstrength-${refIdx}`, (clips) => {
                            const target = clips[clipIdx]?.stages[stageIdx];
                            if (target && refIdx < target.refStrengths.length) {
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
                fields.appendChild(slider);
            });
        }

        const controlNetSlider = buildSlider(
            "ControlNet Strength",
            stage.controlNetStrength,
            STAGE_CONTROLNET_STRENGTH_MIN,
            STAGE_CONTROLNET_STRENGTH_MAX,
            STAGE_CONTROLNET_STRENGTH_STEP,
            (value) => {
                debouncedCommit("controlnet", (clips) => {
                    const target = clips[clipIdx]?.stages[stageIdx];
                    if (target) {
                        target.controlNetStrength = value;
                    }
                });
            },
            { hint: "Only applies when a ControlNet source is set" },
        );
        tagFocus(controlNetSlider, "controlnet");
        fields.appendChild(controlNetSlider);

        fields.appendChild(
            buildLorasSection(clipIdx, stageIdx, stage, defaults),
        );

        railSkipSync = (skipped: boolean): void => {
            const railChip = boundBody?.querySelector<HTMLElement>(
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
        const label = document.createElement("span");
        label.className = "vst-audio-field-label";
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
                remove.className = "vst-stage-lora-remove";
                remove.textContent = "×";
                remove.title = "Remove this LoRA";
                remove.addEventListener("click", () => {
                    flushPending();
                    if (isStale()) {
                        render();
                        return;
                    }
                    const clips = getClips();
                    const target = clips[clipIdx]?.stages[stageIdx];
                    if (!target) {
                        return;
                    }
                    target.loras.splice(index, 1);
                    saveClips(clips);
                    sourceToken = readStateToken();
                    render();
                });
                row.append(select, weight, remove);
                list.appendChild(row);
            });
            section.appendChild(list);

            const addBtn = document.createElement("button");
            addBtn.type = "button";
            addBtn.className = "vst-stage-lora-add";
            addBtn.textContent = "+ Add LoRA";
            addBtn.addEventListener("click", () => {
                flushPending();
                if (isStale()) {
                    render();
                    return;
                }
                const clips = getClips();
                const target = clips[clipIdx]?.stages[stageIdx];
                if (!target) {
                    return;
                }
                target.loras.push({
                    name: defaults.loraValues[0] ?? "",
                    weight: LORA_WEIGHT_DEFAULT,
                });
                saveClips(clips);
                sourceToken = readStateToken();
                render();
            });
            section.appendChild(addBtn);
        }

        return section;
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

        body.appendChild(buildClipColumn(clip, sel.clipIdx));
        body.appendChild(buildStageRail(clip, sel.clipIdx, sel.stageIdx));
        const params = buildParamsColumn(
            clip,
            sel.clipIdx,
            sel.stageIdx,
            stage,
            defaults,
        );
        body.appendChild(params.col);
        return body;
    };

    // --- reference editor -------------------------------------------------

    const buildRefBody = (
        sel: Extract<TimelineSelection, { kind: "ref" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx, refIdx } = sel;
        const clip = clips[clipIdx];
        const ref = clip.refs[refIdx];
        const body = document.createElement("div");
        body.className = "vst-detail-body vst-detail-form-body";

        const frameMax = getReferenceFrameMax(getRootDefaults, clip);
        const options = buildImageSourceOptions(ref.source ?? "");
        const source = resolveImageSourceValue(ref.source ?? "", options);
        const isUpload = source === REF_SOURCE_UPLOAD;

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
        body.appendChild(buildField("Image Source", select));

        if (isUpload) {
            const preview = document.createElement("div");
            preview.className = "vst-refs-thumb-preview";
            const data = ref.uploadedImage?.data;
            if (data) {
                preview.style.backgroundImage = `url('${mediaPreviewSrc(data)}')`;
                preview.classList.add("vst-refs-thumb-preview-set");
            }
            body.appendChild(preview);
        }

        const frameInput = buildNumber(
            ref.frame,
            REF_FRAME_MIN,
            frameMax,
            1,
            (value) => {
                debouncedCommit("ref-frame", (cs) => {
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
        frameInput.setAttribute("data-vst-focus-key", "ref-frame");
        body.appendChild(
            buildField(`Attach at Frame (1–${frameMax})`, frameInput),
        );

        body.appendChild(
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
            body.appendChild(
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

        const del = document.createElement("button");
        del.type = "button";
        del.className = "vst-refs-delete vst-detail-delete";
        del.textContent = "Delete reference";
        del.addEventListener("click", (event) => {
            event.preventDefault();
            deleteRefEntry(clipIdx, refIdx);
        });
        body.appendChild(del);
        return body;
    };

    // --- audio editor -----------------------------------------------------

    const buildAudioBody = (
        sel: Extract<TimelineSelection, { kind: "audio" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx } = sel;
        const clip = clips[clipIdx];
        const controlNetEnabled = `${clip.controlNetLora ?? ""}`.trim() !== "";
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
                const cnEnabled =
                    `${target.controlNetLora ?? ""}`.trim() !== "";
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
        body.className = "vst-detail-body vst-detail-form-body";

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
        );
        if (!canLength) {
            lengthRow.classList.add("vst-audio-disabled");
            lengthRow.querySelector("input")?.setAttribute("disabled", "");
        }
        body.appendChild(lengthRow);

        const saveRow = buildCheckbox(
            "Save Audio Track",
            clip.saveAudioTrack === true && isAce,
            (value) => {
                commitAudio((c) => {
                    c.saveAudioTrack = value;
                });
            },
        );
        if (!isAce) {
            saveRow.classList.add("vst-audio-disabled");
            saveRow.querySelector("input")?.setAttribute("disabled", "");
        }
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
        addSegment.className = "vst-detail-add-segment";
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
        return body;
    };

    // --- audio segment editor ---------------------------------------------

    const buildAudioSegmentBody = (
        sel: Extract<TimelineSelection, { kind: "audio-segment" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx, segIdx } = sel;
        const clip = clips[clipIdx];
        const segment = clip?.audioSegments?.[segIdx];
        const body = document.createElement("div");
        body.className = "vst-detail-body vst-detail-form-body";
        if (!segment) {
            return body;
        }
        const clipDur = Math.max(AUDIO_SEGMENT_MIN_LENGTH, clip.duration || 0);

        const clampSegment = (
            start: number,
            length: number,
        ): { start: number; length: number } => {
            const s = clamp(
                start,
                0,
                Math.max(0, clipDur - AUDIO_SEGMENT_MIN_LENGTH),
            );
            const l = clamp(
                length,
                AUDIO_SEGMENT_MIN_LENGTH,
                Math.max(AUDIO_SEGMENT_MIN_LENGTH, clipDur - s),
            );
            return { start: s, length: l };
        };

        body.appendChild(
            buildUploadRow(
                "Audio Upload",
                "audio/*",
                segment.source?.fileName,
                (data, fileName) => {
                    commit((cs) => {
                        const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                        if (seg) {
                            seg.source = { data, fileName };
                        }
                    });
                    render();
                },
                () => {
                    commit((cs) => {
                        const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                        if (seg) {
                            seg.source = null;
                        }
                    });
                    render();
                },
            ),
        );

        const startInput = buildNumber(
            segment.startSeconds,
            0,
            Math.max(0, clipDur - AUDIO_SEGMENT_MIN_LENGTH),
            AUDIO_SEGMENT_STEP,
            (value) => {
                debouncedCommit("audio-segment-start", (cs) => {
                    const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                    if (seg) {
                        const next = clampSegment(value, seg.lengthSeconds);
                        seg.startSeconds = next.start;
                        seg.lengthSeconds = next.length;
                    }
                });
            },
        );
        startInput.setAttribute("data-vst-focus-key", "audio-segment-start");
        body.appendChild(buildField("Start (s)", startInput));

        const trimInput = buildNumber(
            segment.trimStartSeconds,
            0,
            CLIP_DURATION_MAX,
            AUDIO_SEGMENT_STEP,
            (value) => {
                debouncedCommit("audio-segment-trim", (cs) => {
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
        trimInput.setAttribute("data-vst-focus-key", "audio-segment-trim");
        body.appendChild(buildField("Trim start (s)", trimInput));

        const lengthInput = buildNumber(
            segment.lengthSeconds,
            AUDIO_SEGMENT_MIN_LENGTH,
            clipDur,
            AUDIO_SEGMENT_STEP,
            (value) => {
                debouncedCommit("audio-segment-length", (cs) => {
                    const seg = cs[clipIdx]?.audioSegments?.[segIdx];
                    if (seg) {
                        const next = clampSegment(seg.startSeconds, value);
                        seg.startSeconds = next.start;
                        seg.lengthSeconds = next.length;
                    }
                });
            },
        );
        lengthInput.setAttribute("data-vst-focus-key", "audio-segment-length");
        body.appendChild(buildField("Length (s)", lengthInput));

        const note = document.createElement("p");
        note.className = "vst-detail-note";
        note.textContent =
            "Overlaid additively over the base audio; overlapping segments are mixed.";
        body.appendChild(note);

        const del = document.createElement("button");
        del.type = "button";
        del.className = "vst-refs-delete vst-detail-delete";
        del.textContent = "Remove segment";
        del.addEventListener("click", (event) => {
            event.preventDefault();
            removeAudioSegment(clipIdx, segIdx);
        });
        body.appendChild(del);
        return body;
    };

    // --- prompt editors ---------------------------------------------------

    const buildPromptMajorBody = (
        sel: Extract<TimelineSelection, { kind: "prompt-major" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx } = sel;
        const body = document.createElement("div");
        body.className =
            "vst-detail-body vst-detail-form-body vst-detail-prompt-body";
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
        return body;
    };

    const buildPromptMinorBody = (
        sel: Extract<TimelineSelection, { kind: "prompt-minor" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx, windowIdx } = sel;
        const body = document.createElement("div");
        body.className =
            "vst-detail-body vst-detail-form-body vst-detail-prompt-body";
        body.appendChild(
            buildTextarea(
                clips[clipIdx].promptWindows[windowIdx].prompt ?? "",
                "Minor prompt for this window…",
                "prompt-minor",
                (value) => {
                    debouncedCommit("prompt-minor", (cs) => {
                        const w = cs[clipIdx]?.promptWindows?.[windowIdx];
                        if (w) {
                            w.prompt = value.trim();
                        }
                    });
                },
            ),
        );
        const del = document.createElement("button");
        del.type = "button";
        del.className = "vst-refs-delete vst-detail-delete";
        del.textContent = "Delete prompt window";
        del.addEventListener("click", (event) => {
            event.preventDefault();
            deleteWindowEntry(clipIdx, windowIdx);
        });
        body.appendChild(del);
        return body;
    };

    // --- retake editor ----------------------------------------------------

    const buildRetakeBody = (
        sel: Extract<TimelineSelection, { kind: "retake" }>,
        clips: Clip[],
    ): HTMLElement => {
        const { clipIdx } = sel;
        const clip = clips[clipIdx];
        const retake = clip.retake;
        const body = document.createElement("div");
        body.className = "vst-detail-body vst-detail-form-body";
        if (!retake) {
            return body;
        }
        const clipDur = Math.max(RETAKE_MIN_DURATION, clip.duration || 0);

        const clampRetake = (
            start: number,
            length: number,
        ): { start: number; length: number } => {
            const s = clamp(
                start,
                0,
                Math.max(0, clipDur - RETAKE_MIN_DURATION),
            );
            const l = clamp(
                length,
                RETAKE_MIN_DURATION,
                Math.max(RETAKE_MIN_DURATION, clipDur - s),
            );
            return { start: s, length: l };
        };

        const startInput = buildNumber(
            retake.startSeconds,
            0,
            Math.max(0, clipDur - RETAKE_MIN_DURATION),
            RETAKE_DURATION_STEP,
            (value) => {
                debouncedCommit("retake-start", (cs) => {
                    const r = cs[clipIdx]?.retake;
                    if (r) {
                        const next = clampRetake(value, r.lengthSeconds);
                        r.startSeconds = next.start;
                        r.lengthSeconds = next.length;
                    }
                });
            },
        );
        startInput.setAttribute("data-vst-focus-key", "retake-start");
        body.appendChild(buildField("Start (s)", startInput));

        const lengthInput = buildNumber(
            retake.lengthSeconds,
            RETAKE_MIN_DURATION,
            clipDur,
            RETAKE_DURATION_STEP,
            (value) => {
                debouncedCommit("retake-length", (cs) => {
                    const r = cs[clipIdx]?.retake;
                    if (r) {
                        const next = clampRetake(r.startSeconds, value);
                        r.startSeconds = next.start;
                        r.lengthSeconds = next.length;
                    }
                });
            },
        );
        lengthInput.setAttribute("data-vst-focus-key", "retake-length");
        body.appendChild(buildField("Length (s)", lengthInput));

        body.appendChild(
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
        body.appendChild(note);

        const del = document.createElement("button");
        del.type = "button";
        del.className = "vst-refs-delete vst-detail-delete";
        del.textContent = "Remove retake";
        del.addEventListener("click", (event) => {
            event.preventDefault();
            removeRetake(clipIdx);
        });
        body.appendChild(del);
        return body;
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
        body.className =
            "vst-detail-body vst-detail-form-body vst-detail-boundary";
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
        return body;
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
            : (DIMENSION_PRESET_KEYS.find((key) => {
                  const dims = presetDimensions(key);
                  return (
                      dims &&
                      dims.width === Math.round(state.width) &&
                      dims.height === Math.round(state.height)
                  );
              }) ?? SETTINGS_CUSTOM);
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
        body.className =
            "vst-detail-body vst-detail-form-body vst-detail-settings";

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
        return body;
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
                return buildRetakeBody(sel, clips);
            case "boundary":
                return buildBoundaryBody(sel, clips);
            default:
                return buildSettingsBody();
        }
    };

    const render = (): void => {
        if (!boundBody) {
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
        } finally {
            rendering = false;
        }
    };

    const onSelectionChanged = (sel: TimelineSelection): void => {
        if (suppressSelectionRender) {
            return;
        }
        // A fresh "none" re-derives the settings resolution mode from scratch.
        settingsMode = null;
        if (sel.kind !== "none" && options.isCollapsed()) {
            options.setCollapsed(false);
        }
        render();
    };

    const attach = (body: HTMLElement): void => {
        if (boundBody === body) {
            return;
        }
        dispose();
        boundBody = body;
        body.addEventListener("mousedown", onMouseDownCapture, true);
        body.addEventListener("click", onClickCapture, true);
        body.addEventListener("keydown", onKeyDownCapture, true);
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
            boundBody
                .querySelector<HTMLElement>(`:scope > .${DETAIL_CLASS}`)
                ?.remove();
            boundBody = null;
        }
    };

    return { attach, render, dispose };
};
