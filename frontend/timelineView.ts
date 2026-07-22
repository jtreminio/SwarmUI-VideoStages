import { AUDIO_SOURCE_VOICE_REF, isAceStepFunAudioSource } from "./audioSource";
import { clipHueCss } from "./clipColor";
import { clamp, mediaPreviewSrc } from "./constants";
import { matchPresetKey } from "./dimensionPresets";
import {
    audioSourceBadge,
    chooseRulerStepSeconds,
    computeRulerTicks,
    escapeHtml,
    formatRulerLabel,
    formatTimeLabel,
    keyframeLeftPercent,
    keyframeTimeSeconds,
    refSourceLabel,
    safeFps,
    shortModelName,
    stageChipLabel,
    stageChipTitle,
    type TimelineUnit,
    truncate,
} from "./timelineDetail";
import type {
    AudioSegment,
    BoundaryOut,
    Clip,
    PromptWindow,
    RefImage,
} from "./types";
import { roundToTenth } from "./utils";

export interface RegionLayout {
    index: number;
    startSeconds: number;
    durationSeconds: number;
    startPx: number;
    widthPx: number;
    stageCount: number;
    keyframeCount: number;
    skipped: boolean;
}

export interface RegionLayoutOptions {
    pxPerSecond?: number;
}

export interface RenderTimelineOptions {
    fps?: number;
    width?: number;
    height?: number;
    dimsExplicit?: boolean;
    fpsExplicit?: boolean;
    unit?: TimelineUnit;
    pxPerSecond?: number;
    selectedIndex?: number | null;
    enabled?: boolean;
    onToggleEnabled?: (enabled: boolean) => void;
    onOpenSettings?: (anchor: HTMLElement) => void;
    onToggleUnit?: () => void;
    onAddClip?: () => void;
    onZoomIn?: () => void;
    onZoomOut?: () => void;
    onZoomFit?: () => void;
    onZoomSlider?: (pxPerSecond: number) => void;
    onZoomWheel?: (factor: number, clientX: number) => void;
    onUndo?: () => void;
    onRedo?: () => void;
    globalPrompt?: string;
}

export const DEFAULT_PX_PER_SECOND = 44;
const DEFAULT_MIN_WIDTH_PX = 8;
export const MIN_PX_PER_SECOND = 6;
export const MAX_PX_PER_SECOND = 400;
export const ZOOM_FACTOR = 1.25;
export const TRACK_HEADER_W_PX = 168;

export const waveBarHeights = (clipIdx: number, count: number): number[] => {
    const n = Number.isFinite(count) ? Math.max(0, Math.floor(count)) : 0;
    const heights: number[] = [];
    for (let i = 0; i < n; i++) {
        const raw = Math.sin((clipIdx * 131 + i) * 12.9898) * 43758.5453;
        const fract = raw - Math.floor(raw);
        heights.push(Math.round((20 + fract * 80) * 10) / 10);
    }
    return heights;
};

export const clampPxPerSecond = (value: number): number =>
    Number.isFinite(value)
        ? Math.min(MAX_PX_PER_SECOND, Math.max(MIN_PX_PER_SECOND, value))
        : DEFAULT_PX_PER_SECOND;

export const zoomAnchorTime = (
    offsetX: number,
    scrollLeft: number,
    pxPerSecond: number,
    headerW = TRACK_HEADER_W_PX,
): number => {
    if (pxPerSecond <= 0) {
        return 0;
    }
    const effOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, (effOffsetX + scrollLeft - headerW) / pxPerSecond);
};

export const zoomAnchorScrollLeft = (
    time: number,
    pxPerSecond: number,
    offsetX: number,
    headerW = TRACK_HEADER_W_PX,
): number => {
    const effOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, headerW + time * pxPerSecond - effOffsetX);
};

export const computeFitPxPerSecond = (
    totalSeconds: number,
    containerWidthPx: number,
    padPx = 24,
): number => {
    if (totalSeconds <= 0 || containerWidthPx <= padPx) {
        return DEFAULT_PX_PER_SECOND;
    }
    return clampPxPerSecond((containerWidthPx - padPx) / totalSeconds);
};

export const computeRegionLayout = (
    clips: Clip[],
    options?: RegionLayoutOptions,
): RegionLayout[] => {
    const pxPerSecond = options?.pxPerSecond ?? DEFAULT_PX_PER_SECOND;
    const minWidthPx = DEFAULT_MIN_WIDTH_PX;
    const layouts: RegionLayout[] = [];
    let cursorSeconds = 0;
    let cursorPx = 0;
    for (let index = 0; index < clips.length; index++) {
        const clip = clips[index];
        const durationSeconds = Math.max(0, clip.duration || 0);
        const widthPx = Math.max(minWidthPx, durationSeconds * pxPerSecond);
        layouts.push({
            index,
            startSeconds: cursorSeconds,
            durationSeconds,
            startPx: cursorPx,
            widthPx,
            stageCount: (clip.stages ?? []).length,
            keyframeCount: (clip.refs ?? []).length,
            skipped: clip.skipped === true,
        });
        cursorSeconds += durationSeconds;
        cursorPx += durationSeconds * pxPerSecond;
    }
    return layouts;
};

const refFrame = (ref: RefImage): number => Math.max(0, ref.frame ?? 0);

/**
 * Ambient 2-cell filmstrip drawn behind the region content: the earliest ref
 * (lowest frame, not from-end) as a left cell, and — when a distinct end ref
 * exists (a from-end ref, or the highest-frame ref if it differs) — a right
 * cell. Low opacity, pointer-events none; sits behind all region chrome. Clips
 * with no ref images render nothing.
 */
const renderRegionThumb = (clip: Clip): string => {
    const withImage = (clip.refs ?? []).filter(
        (ref) => !!ref.uploadedImage?.data,
    );
    if (withImage.length === 0) {
        return "";
    }
    const startPool = withImage.filter((ref) => ref.fromEnd !== true);
    const startRef = (startPool.length > 0 ? startPool : withImage).reduce(
        (best, ref) => (refFrame(ref) < refFrame(best) ? ref : best),
    );
    let endRef: RefImage | null =
        withImage.find((ref) => ref.fromEnd === true) ?? null;
    if (!endRef) {
        const highest = withImage.reduce((best, ref) =>
            refFrame(ref) > refFrame(best) ? ref : best,
        );
        if (highest !== startRef) {
            endRef = highest;
        }
    }
    const cell = (ref: RefImage, side: "start" | "end"): string => {
        const src = mediaPreviewSrc(ref.uploadedImage?.data ?? "");
        return `<div class="vst-region-thumb-cell vst-region-thumb-${side}" style="background-image:url('${escapeHtml(src)}')"></div>`;
    };
    const cells = cell(startRef, "start") + (endRef ? cell(endRef, "end") : "");
    const cellCount = endRef ? 2 : 1;
    return `<div class="vst-region-thumb" data-cells="${cellCount}" aria-hidden="true">${cells}</div>`;
};

/**
 * Shared markup for a draggable window span on a clip lane (retake overlay,
 * audio segment): clamped left/width percentages, two resize grips, a label,
 * and the shift-click-delete affordance in the tooltip.
 */
const renderWindowSpan = (opts: {
    className: string;
    dataAttrs: string;
    edgeAttr: string;
    labelClass: string;
    label: string;
    title: string;
    ariaLabel: string;
    startSeconds: number;
    lengthSeconds: number;
    durationSeconds: number;
}): string => {
    const start = clamp(opts.startSeconds, 0, opts.durationSeconds);
    const end = clamp(
        opts.startSeconds + opts.lengthSeconds,
        start,
        opts.durationSeconds,
    );
    if (end <= start) {
        return "";
    }
    const left = (start / opts.durationSeconds) * 100;
    const width = ((end - start) / opts.durationSeconds) * 100;
    return (
        `<div class="${opts.className}" ${opts.dataAttrs} style="left:${left}%;width:${width}%" role="button" tabindex="0" title="${escapeHtml(opts.title)}" aria-label="${escapeHtml(opts.ariaLabel)}">` +
        `<span class="${opts.className}-resize ${opts.className}-resize-l" ${opts.edgeAttr}="left" aria-hidden="true"></span>` +
        `<span class="${opts.labelClass}">${escapeHtml(opts.label)}</span>` +
        `<span class="${opts.className}-resize ${opts.className}-resize-r" ${opts.edgeAttr}="right" aria-hidden="true"></span>` +
        `</div>`
    );
};

const renderRetakeOverlay = (
    clip: Clip,
    clipIdx: number,
    durationSeconds: number,
): string => {
    const retake = clip.retake;
    if (!retake || durationSeconds <= 0) {
        return "";
    }
    const start = clamp(retake.startSeconds, 0, durationSeconds);
    const end = clamp(
        retake.startSeconds + retake.lengthSeconds,
        start,
        durationSeconds,
    );
    const label = `RETAKE ${roundToTenth(start)}–${roundToTenth(end)} s`;
    return renderWindowSpan({
        className: "vst-retake",
        dataAttrs: `data-vst-retake data-clip-idx="${clipIdx}"`,
        edgeAttr: "data-vst-retake-edge",
        labelClass: "vst-retake-label",
        label,
        title: `${label} · drag to move/resize · Shift+click to delete`,
        ariaLabel: label,
        startSeconds: retake.startSeconds,
        lengthSeconds: retake.lengthSeconds,
        durationSeconds,
    });
};

const renderKeyframes = (
    clip: Clip,
    clipIdx: number,
    durationSeconds: number,
    fps: number,
    unit: TimelineUnit,
): string => {
    const refs = clip.refs ?? [];
    if (refs.length === 0) {
        return "";
    }
    // Purely visual markers mirroring the References track — editing (drag,
    // from-end toggle, delete) happens on the track's thumbnails, never here.
    const pips = refs
        .map((ref: RefImage, refIdx: number) => {
            const time = keyframeTimeSeconds(
                ref.frame,
                ref.fromEnd === true,
                durationSeconds,
                fps,
            );
            const left = keyframeLeftPercent(time, durationSeconds);
            const isEnd = ref.fromEnd === true;
            const isPrimary = (ref.frame ?? 0) === 1 && !isEnd;
            const source = refSourceLabel(ref.source ?? "");
            const title = `${source} · frame ${ref.frame ?? 0}${isEnd ? " (from end)" : ""}${isPrimary ? " (cover)" : ""} · ${formatTimeLabel(time, unit, fps)}`;
            const kindClass =
                (isEnd ? " vst-key-end" : " vst-key-start") +
                (isPrimary ? " vst-key-primary" : "");
            return (
                `<span class="vst-key${kindClass}" data-clip-idx="${clipIdx}" data-ref-idx="${refIdx}" style="left:${left}%" title="${escapeHtml(title)}" aria-hidden="true">` +
                `<span class="vst-key-dot" aria-hidden="true"></span>` +
                `</span>`
            );
        })
        .join("");
    return `<div class="vst-keys" title="Reference markers">${pips}</div>`;
};

const renderBadges = (clip: Clip, clipIdx: number): string => {
    const stage0 = (clip.stages ?? [])[0];
    if (!stage0) {
        return `<div class="vst-badges"></div>`;
    }
    const model = stage0.model ?? "";
    const short = shortModelName(model);
    const full = `${model}`.trim() || "(default)";
    const title = `Clip model: ${full} — click to change (applies to Stage 0)`;
    const badge =
        `<span class="vst-badge vst-badge-model" data-vst-model data-clip-idx="${clipIdx}" role="button" tabindex="0" title="${escapeHtml(title)}" aria-label="${escapeHtml(title)}">` +
        `${escapeHtml(short)}</span>`;
    const icCount = (clip.icLoras ?? []).length;
    const icTitle = `${icCount} IC-LoRA${icCount === 1 ? "" : "s"} on this clip — edit in the clip panel`;
    const icBadge =
        icCount > 0
            ? `<span class="vst-badge vst-badge-iclora" title="${escapeHtml(icTitle)}" aria-label="${escapeHtml(icTitle)}">IC×${icCount}</span>`
            : "";
    return `<div class="vst-badges">${badge}${icBadge}</div>`;
};

const renderStageChips = (clip: Clip, clipIdx: number): string => {
    const stages = clip.stages ?? [];
    const chips = stages
        .map((stage, stageIdx) => {
            const skipped = stage?.skipped === true;
            const skippedClass = skipped ? " vst-stage-chip-skipped" : "";
            const title = `${stageChipTitle(stage, stageIdx)}${skipped ? " (skipped)" : ""} · click to edit · Shift+click to delete`;
            const label = `${skipped ? "⊘ " : ""}${stageChipLabel(stageIdx)}`;
            return (
                `<span class="vst-chip vst-stage-chip${skippedClass}" data-vst-stage data-clip-idx="${clipIdx}" data-stage-idx="${stageIdx}" role="button" tabindex="0" title="${escapeHtml(title)}">` +
                `${escapeHtml(label)}</span>`
            );
        })
        .join("");
    // No add-stage chip here: stages are added from the dock's stage rail.
    return chips;
};

const lengthDerived = (clip: Clip): boolean =>
    clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true;

// Per-boundary glyphs/labels. The word label lives in the title/aria, not the chip face, so it never
// overflows under overflow:hidden.
export const BOUNDARY_GLYPH: Record<BoundaryOut, string> = {
    cut: "│",
    continue: "→",
    crossfade: "⤬",
};
export const BOUNDARY_LABEL: Record<BoundaryOut, string> = {
    cut: "Cut",
    continue: "Continue",
    crossfade: "Crossfade",
};

/**
 * Chips sitting on each interior seam (between clip N and N+1) that select clip N's outgoing boundary
 * for the detail strip, whose boundary section edits the join mode. Omitted after the final clip (no
 * following clip). `data-vst-boundary-chip` + `data-left-clip-idx` drive timelineBoundaryTrack.
 */
export const renderBoundarySeams = (
    clips: Clip[],
    layouts: RegionLayout[],
): string => {
    const seams: string[] = [];
    for (let i = 1; i < layouts.length; i++) {
        const leftClipIdx = i - 1;
        const clip = clips[leftClipIdx];
        if (!clip) {
            continue;
        }
        const value: BoundaryOut = clip.boundaryOut ?? "cut";
        const glyph = BOUNDARY_GLYPH[value] ?? BOUNDARY_GLYPH.cut;
        const label = BOUNDARY_LABEL[value] ?? BOUNDARY_LABEL.cut;
        const title = `Boundary clip ${leftClipIdx} → ${i}: ${label}. Click to edit.`;
        const ariaLabel = `Clip ${leftClipIdx} outgoing boundary: ${label}. Click to edit.`;
        seams.push(
            `<button type="button" class="basic-button vst-boundary-chip vst-boundary-${value}" data-vst-boundary-chip data-left-clip-idx="${leftClipIdx}" data-boundary="${value}" style="left:${layouts[i].startPx}px" title="${escapeHtml(title)}" aria-label="${escapeHtml(ariaLabel)}">` +
                `<span class="vst-boundary-glyph" aria-hidden="true">${escapeHtml(glyph)}</span>` +
                `</button>`,
        );
    }
    return seams.join("");
};

export interface PromptWindowGeom {
    startSec: number;
    endSec: number;
    leftPx: number;
    widthPx: number;
    active: boolean;
}

const promptWindowGeom = (
    layout: RegionLayout,
    window: PromptWindow,
    pxPerSecond: number,
): PromptWindowGeom => {
    const clipDur = Math.max(0, layout.durationSeconds);
    const startSec = clamp(window.start, 0, clipDur);
    const endSec = clamp(window.start + window.duration, startSec, clipDur);
    return {
        startSec,
        endSec,
        leftPx: startSec * pxPerSecond,
        widthPx: Math.max(2, (endSec - startSec) * pxPerSecond),
        active: `${window.prompt ?? ""}`.trim() !== "",
    };
};

/** Usable inner width of a clip lane (the 2px is the region border). */
const clipInnerWidth = (widthPx: number): number => Math.max(1, widthPx - 2);

/**
 * Small non-interactive pill in a track head, vertically aligned to the
 * mini-lane it names (placement comes from the per-track vst-head-tag-<kind>
 * CSS rule, which copies that lane's top/bottom/height math). "active" tints
 * the pill to hint the lane currently carries content; "muted" dims it (the
 * blank add-lane, or a single-lane track that needs no pointing).
 */
const headTag = (
    kind: string,
    label: string,
    opts?: { active?: boolean; muted?: boolean; style?: string },
): string => {
    const cls =
        `vst-head-tag vst-head-tag-${kind}` +
        (opts?.active ? " vst-head-tag-active" : "") +
        (opts?.muted ? " vst-head-tag-muted" : "");
    const style = opts?.style ? ` style="${opts.style}"` : "";
    return (
        `<div class="${cls}"${style} aria-hidden="true">` +
        `<span class="vst-head-tag-pill">${label}</span>` +
        `<span class="vst-head-tag-tick"></span>` +
        `</div>`
    );
};

const renderTrackHead = (
    iconClass: string,
    icon: string,
    title: string,
    tags: string,
): string =>
    `<div class="vst-track-head">` +
    `<div class="vst-head-top">` +
    `<div class="vst-track-icon ${iconClass}" aria-hidden="true">${icon}</div>` +
    `<div class="vst-track-label"><strong>${title}</strong></div>` +
    `</div>` +
    `<div class="vst-head-tags">${tags}</div>` +
    `</div>`;

const PROMPT_PLACEHOLDER = "(no prompt)";

export const renderPromptTrackRow = (
    clips: Clip[],
    layouts: RegionLayout[],
    pxPerSecond: number,
    globalPrompt: string,
): string => {
    const globalTrimmed = `${globalPrompt ?? ""}`.trim();
    const parts: string[] = [];
    for (let i = 0; i < layouts.length; i++) {
        const layout = layouts[i];
        const clip = clips[i];
        if (!clip) {
            continue;
        }
        const clipWidth = clipInnerWidth(layout.widthPx);
        const windows = clip.promptWindows ?? [];

        const ownPrompt = `${clip.prompt ?? ""}`.trim();
        const inherited = ownPrompt === "";
        const major = inherited ? globalTrimmed : ownPrompt;
        const overlays = windows
            .map((w) => promptWindowGeom(layout, w, pxPerSecond))
            .filter((g) => g.active && g.endSec > g.startSec)
            .map(
                (g) =>
                    `<div class="vst-major-off" style="left:${g.leftPx}px;width:${g.widthPx}px" aria-hidden="true"></div>`,
            )
            .join("");
        const majorText =
            major === "" ? PROMPT_PLACEHOLDER : truncate(major, 120);
        const majorClass =
            (major === "" ? " vst-major-empty" : "") +
            (inherited && major !== "" ? " vst-major-inherited" : "");
        const majorTitle =
            (major === "" ? PROMPT_PLACEHOLDER : major) +
            (inherited && major !== ""
                ? " — inherited from the global prompt; click to set a clip prompt"
                : " — click to edit");
        parts.push(
            `<div class="vst-major-seg${majorClass}" data-vst-prompt="major" data-clip-idx="${i}" style="left:${layout.startPx}px;width:${clipWidth}px" title="${escapeHtml(majorTitle)}">` +
                overlays +
                `<span class="vst-major-text">${escapeHtml(majorText)}</span>` +
                `</div>`,
        );

        const minorSegs = windows
            .map((w, j) => {
                const g = promptWindowGeom(layout, w, pxPerSecond);
                const text = `${w.prompt ?? ""}`.trim();
                const label = text === "" ? "(empty)" : truncate(text, 60);
                return (
                    `<div class="vst-minor-seg" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${j}" style="left:${g.leftPx}px;width:${g.widthPx}px" title="${escapeHtml(`${text || "(empty minor prompt)"} · Shift+click to delete`)}">` +
                    `<span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left" aria-hidden="true"></span>` +
                    `<span class="vst-minor-text">${escapeHtml(label)}</span>` +
                    `<span class="vst-minor-resize vst-minor-resize-r" data-vst-minor-edge="right" aria-hidden="true"></span>` +
                    `</div>`
                );
            })
            .join("");
        parts.push(
            `<div class="vst-minor-lane" data-vst-prompt-add data-clip-idx="${i}" style="left:${layout.startPx}px;width:${clipWidth}px" title="Click empty space to add a minor prompt">${minorSegs}</div>`,
        );
    }
    return (
        `<div class="vst-track-row vst-track-prompt">` +
        renderTrackHead(
            "vst-track-icon-prompt",
            "✎",
            "Prompt",
            headTag("major", "Major", { active: true }) +
                headTag("relay", "Relay", {
                    active: clips.some(
                        (c) => (c.promptWindows?.length ?? 0) > 0,
                    ),
                }),
        ) +
        `<div class="vst-track-cell vst-prompt-cell">${parts.join("")}</div>` +
        `</div>`
    );
};

const audioFlagChips = (clip: Clip): string => {
    const chips: string[] = [];
    if (clip.reuseAudio === true) {
        chips.push(
            `<span class="vst-audio-flag" title="Reuse the first stage's audio latent for later stages">↻</span>`,
        );
    }
    if (clip.clipLengthFromAudio === true) {
        chips.push(
            `<span class="vst-audio-flag" title="Clip length follows the audio length">⇥</span>`,
        );
    }
    if (clip.saveAudioTrack === true) {
        chips.push(
            `<span class="vst-audio-flag" title="Save a standalone MP3 for this clip's audio">MP3</span>`,
        );
    }
    return chips.length === 0
        ? ""
        : `<span class="vst-audio-flags" aria-hidden="true">${chips.join("")}</span>`;
};

/**
 * Overlay audio-segment spans drawn inside a clip's audio cell: draggable body
 * (move), left/right resize grips, click to select, shift+click to delete.
 * Positioned as a percentage of the clip duration so they track the cell width.
 */
const renderAudioSegmentBlock = (
    seg: AudioSegment,
    clipIdx: number,
    segIdx: number,
    durationSeconds: number,
): string => {
    const start = clamp(seg.startSeconds, 0, durationSeconds);
    const end = clamp(
        seg.startSeconds + seg.lengthSeconds,
        start,
        durationSeconds,
    );
    const name =
        typeof seg.source === "string" ? seg.source : seg.source?.fileName;
    const labelText = name ? name : "audio segment";
    const label = `${roundToTenth(start)}–${roundToTenth(end)} s`;
    return renderWindowSpan({
        className: "vst-audio-seg",
        dataAttrs: `data-vst-audio-seg data-clip-idx="${clipIdx}" data-seg-idx="${segIdx}"`,
        edgeAttr: "data-vst-audio-seg-edge",
        labelClass: "vst-audio-seg-label",
        label: labelText,
        title: `${labelText} · ${label} · drag to move/resize · Shift+click to delete`,
        ariaLabel: `Edit audio segment ${segIdx} for clip ${clipIdx}`,
        startSeconds: seg.startSeconds,
        lengthSeconds: seg.lengthSeconds,
        durationSeconds,
    });
};

/**
 * One mini lane per segment (the array index IS the lane), plus one BLANK lane
 * at the bottom that adds a segment on click/drag — the new segment takes the
 * blank lane over and a fresh blank lane appears beneath it. Per-lane segments
 * may overlap in time; the backend mixes them additively.
 */
const renderAudioSegmentLanes = (
    clip: Clip,
    clipIdx: number,
    durationSeconds: number,
    startPx: number,
    widthPx: number,
): string => {
    const place = (laneIdx: number): string =>
        `left:${startPx}px;width:${widthPx}px;--vst-audio-lane-idx:${laneIdx}`;
    const blankLane = (laneIdx: number): string =>
        `<div class="vst-audio-seg-lane vst-audio-seg-lane-blank" data-vst-audio-seg-add data-clip-idx="${clipIdx}" ` +
        `style="${place(laneIdx)}" title="Click or drag to add an audio segment"></div>`;
    if (durationSeconds <= 0) {
        return blankLane(0);
    }
    const segments = clip.audioSegments ?? [];
    const lanes = segments.map(
        (seg, segIdx) =>
            `<div class="vst-audio-seg-lane" style="${place(segIdx)}">` +
            renderAudioSegmentBlock(seg, clipIdx, segIdx, durationSeconds) +
            `</div>`,
    );
    lanes.push(blankLane(segments.length));
    return lanes.join("");
};

export const renderAudioTrackRow = (
    clips: Clip[],
    layouts: RegionLayout[],
): string => {
    const segments = layouts
        .map((l) => {
            const clip = clips[l.index];
            if (!clip) {
                return "";
            }
            const badge = audioSourceBadge(clip.audioSource ?? "");
            const native = badge.label === "Native";
            const width = clipInnerWidth(l.widthPx);
            // Per-source tinting: every source kind renders a full-width fake
            // waveform in its own color so the track reads at a glance.
            const kindClass = native
                ? " vst-audio-native vst-audio-kind-native"
                : isAceStepFunAudioSource(clip.audioSource ?? "")
                  ? " vst-audio-kind-ace"
                  : clip.audioSource === AUDIO_SOURCE_VOICE_REF
                    ? " vst-audio-kind-voiceref"
                    : " vst-audio-kind-upload";
            const upload =
                !native &&
                (clip.audioSource === "Upload" ||
                    clip.audioSource === AUDIO_SOURCE_VOICE_REF)
                    ? clip.uploadedAudio?.fileName
                    : null;
            const labelText = upload
                ? `${badge.label} · ${upload}`
                : badge.label;
            const title = native
                ? "Audio: Native — click to choose an audio source"
                : `${badge.title} — click to edit`;
            const barCount = Math.min(
                400,
                Math.max(8, Math.floor(width / 5.5)),
            );
            const bars = waveBarHeights(l.index, barCount)
                .map((h) => `<span style="height:${h}%"></span>`)
                .join("");
            // Native keeps its hover call-to-action on top of the waveform.
            const hint = native
                ? `<span class="vst-audio-hint" aria-hidden="true">click to add audio</span>`
                : "";
            const body = `<div class="vst-audio-wave" aria-hidden="true">${bars}</div>${hint}`;
            return (
                `<div class="vst-audio-clip${kindClass}" data-vst-audio="clip" data-clip-idx="${l.index}" role="button" tabindex="0" style="left:${l.startPx}px;width:${width}px" title="${escapeHtml(title)}" aria-label="Edit audio for clip ${l.index}">` +
                `<span class="vst-audio-label">${escapeHtml(labelText)}</span>` +
                audioFlagChips(clip) +
                body +
                `</div>` +
                renderAudioSegmentLanes(
                    clip,
                    l.index,
                    clip.duration || 0,
                    l.startPx,
                    width,
                )
            );
        })
        .join("");
    // The row grows with the busiest clip: N segment lanes + the blank lane.
    const laneCount = Math.max(
        1,
        ...clips.map((clip) => (clip.audioSegments?.length ?? 0) + 1),
    );
    // One tag per lane index, mirroring renderAudioSegmentLanes: the last
    // lane is always the blank add-lane, so its tag is the muted "+".
    const laneTags = [headTag("src", "Src", { active: true })];
    for (let i = 0; i < laneCount; i++) {
        const blank = i === laneCount - 1;
        laneTags.push(
            headTag("seg", blank ? "+" : `S${i + 1}`, {
                active: !blank,
                muted: blank,
                style: `--vst-audio-lane-idx:${i}`,
            }),
        );
    }
    return (
        `<div class="vst-track-row vst-track-audio" style="--vst-audio-lane-count:${laneCount}">` +
        renderTrackHead(
            "vst-track-icon-audio",
            "♪",
            "Audio",
            laneTags.join(""),
        ) +
        `<div class="vst-track-cell vst-audio-cell">${segments}</div>` +
        `</div>`
    );
};

const REF_EDGE_ALIGN_FRAMES = 3;

export const renderReferencesTrackRow = (
    clips: Clip[],
    layouts: RegionLayout[],
    fps: number,
    unit: TimelineUnit,
): string => {
    const lanes = layouts
        .map((l) => {
            const clip = clips[l.index];
            if (!clip) {
                return "";
            }
            const laneWidth = clipInnerWidth(l.widthPx);
            const marks = (clip.refs ?? [])
                .map((ref: RefImage, refIdx: number) => {
                    const isEnd = ref.fromEnd === true;
                    const frame = Math.max(0, ref.frame ?? 0);
                    const isPrimary = frame === 1 && !isEnd;
                    const time = keyframeTimeSeconds(
                        ref.frame,
                        isEnd,
                        l.durationSeconds,
                        fps,
                    );
                    const left = keyframeLeftPercent(time, l.durationSeconds);
                    const source = refSourceLabel(ref.source ?? "");
                    const image = ref.uploadedImage?.data;
                    const thumbStyle = image
                        ? ` style="background-image:url('${escapeHtml(mediaPreviewSrc(image))}')"`
                        : "";
                    const frameLabel = `R ${isEnd ? "-" : ""}${frame}`;
                    const thumbClass = `vst-refs-thumb${image ? " vst-refs-has-image" : ""}`;
                    const thumbInner = `<span class="vst-refs-ph">${escapeHtml(frameLabel)}</span>`;
                    const alignClass =
                        frame > REF_EDGE_ALIGN_FRAMES
                            ? ""
                            : isEnd
                              ? " vst-refs-align-end"
                              : " vst-refs-align-start";
                    const kindClass =
                        (isPrimary ? " vst-refs-primary" : "") +
                        (isEnd ? " vst-refs-fromend" : "") +
                        alignClass;
                    const title =
                        `${source}${isPrimary ? " · cover frame" : ""}${isEnd ? " · from end" : ""}` +
                        ` · frame ${frame} · ${formatTimeLabel(time, unit, fps)}` +
                        ` · click to edit, drag to move · Shift+click to delete`;
                    const label = `Edit reference ${refIdx} (${source}${isEnd ? ", from end" : ""})`;
                    return (
                        `<div class="vst-refs-mark${kindClass}" data-vst-ref="thumb" data-clip-idx="${l.index}" data-ref-idx="${refIdx}" style="left:${left}%" role="button" tabindex="0" title="${escapeHtml(title)}" aria-label="${escapeHtml(label)}">` +
                        `<span class="${thumbClass}"${thumbStyle}>${thumbInner}</span>` +
                        `</div>`
                    );
                })
                .join("");
            return `<div class="vst-refs-lane" data-vst-ref-add data-clip-idx="${l.index}" style="left:${l.startPx}px;width:${laneWidth}px" title="Click to add a reference image at this frame">${marks}</div>`;
        })
        .join("");
    return (
        `<div class="vst-track-row vst-track-refs">` +
        renderTrackHead(
            "vst-track-icon-refs",
            "⧉",
            "References",
            headTag("refs", "Refs", {
                active: clips.some((c) => (c.refs?.length ?? 0) > 0),
                muted: true,
            }),
        ) +
        `<div class="vst-track-cell">${lanes}</div>` +
        `</div>`
    );
};

export const renderTimeline = (
    body: HTMLElement,
    clips: Clip[],
    options?: RenderTimelineOptions,
): void => {
    const fps = safeFps(options?.fps);
    const unit: TimelineUnit =
        options?.unit === "frames" ? "frames" : "seconds";
    const pxPerSecond = clampPxPerSecond(
        options?.pxPerSecond ?? DEFAULT_PX_PER_SECOND,
    );
    body.dataset.vstPps = String(pxPerSecond);
    body.dataset.vstFps = String(fps);

    const layouts = computeRegionLayout(clips, { pxPerSecond });
    const totalSeconds = layouts.reduce((sum, l) => sum + l.durationSeconds, 0);
    const totalPx = layouts.reduce(
        (max, l) => Math.max(max, l.startPx + l.widthPx),
        0,
    );

    const toggleLabel = unit === "frames" ? "Show seconds" : "Show frames";
    const clipWord = `clip${clips.length === 1 ? "" : "s"}`;
    const totalLabel = escapeHtml(formatTimeLabel(totalSeconds, unit, fps));
    const zoomPct = Math.round((pxPerSecond / DEFAULT_PX_PER_SECOND) * 100);
    const rawSelected = options?.selectedIndex;
    const selectedIndex =
        typeof rawSelected === "number" &&
        Number.isInteger(rawSelected) &&
        rawSelected >= 0 &&
        rawSelected < clips.length
            ? rawSelected
            : null;
    const selHidden = selectedIndex === null ? " hidden" : "";
    const readout =
        `<span class="vst-readout" data-vst-readout>` +
        `<span title="Sequence total">${totalLabel} total</span>` +
        `<span class="vst-dot" data-vst-readout-sel-dot${selHidden}>·</span>` +
        `<span class="vst-readout-sel" data-vst-readout-sel title="Selected clip"${selHidden}>${selectedIndex !== null ? `clip ${selectedIndex}` : ""}</span>` +
        `</span>`;
    const chipWidth = Math.max(0, Math.round(options?.width ?? 0));
    const chipHeight = Math.max(0, Math.round(options?.height ?? 0));
    const chipFps = fps;
    const chipDimsExplicit = options?.dimsExplicit === true;
    const chipFpsExplicit = options?.fpsExplicit === true;
    const chipPresetKey =
        chipDimsExplicit && chipWidth > 0 && chipHeight > 0
            ? matchPresetKey(chipWidth, chipHeight)
            : null;
    const dimsSource = chipDimsExplicit
        ? chipPresetKey
            ? `${chipPresetKey} preset`
            : "custom"
        : "inherited from image resolution";
    const fpsSource = chipFpsExplicit ? "custom" : "inherited from Video FPS";
    const settingsTip = `Resolution: ${dimsSource}; FPS: ${fpsSource}. Click to edit.`;
    const settingsChip =
        `<button type="button" class="basic-button small-button vst-settings-chip" data-vst-settings title="${escapeHtml(settingsTip)}" aria-label="${escapeHtml(settingsTip)}">` +
        `<span class="vst-settings-dims">${chipWidth}×${chipHeight}</span>` +
        `<span class="vst-settings-chip-sep" aria-hidden="true">·</span>` +
        `<span class="vst-settings-fps">${chipFps} fps</span>` +
        `</button>`;

    const enabled = options?.enabled !== false;
    const enableToggle =
        `<label class="vst-enable" title="Enable VideoStages. While off, none of this timeline is sent to the backend — a normal image/video generates as usual.">` +
        // The host's .toggle-switch composite (site.css) — the input is the
        // invisible hit area, the -content div is the drawn switch.
        `<span class="toggle-switch">` +
        `<input type="checkbox" class="auto-slider-toggle vst-enable-input" role="switch" data-vst-enable${enabled ? " checked" : ""}>` +
        `<div class="auto-slider-toggle-content"></div>` +
        `</span>` +
        `<span class="vst-enable-label">Enable</span>` +
        `</label>`;
    const header =
        `<div class="vst-topbar${enabled ? "" : " vst-topbar-disabled"}">` +
        `<div class="vst-topbar-main">` +
        `<span class="vst-title">Timeline</span>` +
        enableToggle +
        `<span class="vst-sub"><span class="vst-stat-num">${clips.length}</span> ${clipWord}</span>` +
        settingsChip +
        `</div>` +
        `<div class="vst-topbar-tools">` +
        `<button type="button" class="basic-button small-button btn-primary vst-add-clip" data-vst-add-clip title="Add a new clip to the end of the sequence">+ Clip</button>` +
        `<span class="vst-tool-sep" aria-hidden="true"></span>` +
        `<div class="vst-zoom" role="group" aria-label="Timeline zoom (Ctrl+wheel over the track)">` +
        `<button type="button" class="basic-button small-button" data-vst-zoom-out title="Zoom out (show more time)" aria-label="Zoom out">−</button>` +
        `<span class="vst-zoom-pct" data-vst-zoom-pct title="Zoom level (100% = default)">${zoomPct}%</span>` +
        `<input type="range" class="vst-zoom-slider" data-vst-zoom-slider min="${MIN_PX_PER_SECOND}" max="${MAX_PX_PER_SECOND}" step="1" value="${Math.round(pxPerSecond)}" aria-label="Zoom (pixels per second)" title="Zoom (applies on release)">` +
        `<button type="button" class="basic-button small-button" data-vst-zoom-in title="Zoom in (show less time, more detail)" aria-label="Zoom in">+</button>` +
        `<button type="button" class="basic-button small-button" data-vst-zoom-fit title="Fit the whole sequence to the view" aria-label="Zoom to fit">Fit</button>` +
        `</div>` +
        `<span class="vst-tool-sep" aria-hidden="true"></span>` +
        `<button type="button" class="basic-button small-button vst-toggle-unit" data-vst-unit-toggle title="Toggle ruler units between seconds and frames (in-memory only)">${toggleLabel}</button>` +
        `<button type="button" class="basic-button small-button vst-hist-btn" data-vst-undo title="Undo (Ctrl+Z)" aria-label="Undo">↶</button>` +
        `<button type="button" class="basic-button small-button vst-hist-btn" data-vst-redo title="Redo (Ctrl+Shift+Z or Ctrl+Y)" aria-label="Redo">↷</button>` +
        `</div>` +
        readout +
        `</div>`;

    const wireTopbar = (): void => {
        const wire = (
            selector: string,
            handler: (() => void) | undefined,
        ): void => {
            if (!handler) {
                return;
            }
            const btn = body.querySelector(selector);
            if (btn) {
                btn.addEventListener("click", () => handler());
            }
        };
        const enableInput =
            body.querySelector<HTMLInputElement>("[data-vst-enable]");
        if (enableInput && options?.onToggleEnabled) {
            enableInput.addEventListener("change", () => {
                options.onToggleEnabled?.(enableInput.checked);
            });
        }
        const settingsBtn = body.querySelector<HTMLElement>(
            "[data-vst-settings]",
        );
        if (settingsBtn && options?.onOpenSettings) {
            settingsBtn.addEventListener("click", () => {
                options.onOpenSettings?.(settingsBtn);
            });
        }
        wire("[data-vst-unit-toggle]", options?.onToggleUnit);
        wire("[data-vst-zoom-in]", options?.onZoomIn);
        wire("[data-vst-zoom-out]", options?.onZoomOut);
        wire("[data-vst-zoom-fit]", options?.onZoomFit);
        wire("[data-vst-undo]", options?.onUndo);
        wire("[data-vst-redo]", options?.onRedo);
        const slider = body.querySelector<HTMLInputElement>(
            "[data-vst-zoom-slider]",
        );
        if (slider) {
            slider.addEventListener("input", () => {
                const pct = body.querySelector<HTMLElement>(
                    "[data-vst-zoom-pct]",
                );
                if (pct) {
                    const value = Number.parseFloat(slider.value);
                    pct.textContent = `${Math.round((value / DEFAULT_PX_PER_SECOND) * 100)}%`;
                }
            });
            if (options?.onZoomSlider) {
                slider.addEventListener("change", () => {
                    options.onZoomSlider?.(Number.parseFloat(slider.value));
                });
            }
        }
        if (options?.onAddClip) {
            for (const btn of body.querySelectorAll("[data-vst-add-clip]")) {
                btn.addEventListener("click", () => options.onAddClip?.());
            }
        }
    };

    const wireScroll = (): void => {
        const onZoomWheel = options?.onZoomWheel;
        if (!onZoomWheel) {
            return;
        }
        const scroll = body.querySelector<HTMLElement>(".vst-scroll");
        scroll?.addEventListener(
            "wheel",
            (event: WheelEvent) => {
                if (!event.ctrlKey && !event.metaKey) {
                    return;
                }
                event.preventDefault();
                const factor = event.deltaY < 0 ? ZOOM_FACTOR : 1 / ZOOM_FACTOR;
                onZoomWheel(factor, event.clientX);
            },
            { passive: false },
        );
    };

    if (clips.length === 0) {
        body.innerHTML =
            `${header}<div class="vst-empty">` +
            `<div class="vst-empty-icon" aria-hidden="true">🎬</div>` +
            `<div class="vst-empty-title">No clips yet.</div>` +
            `<div class="vst-empty-hint">Add one here — or in the VideoStages panel on the left — to start building your sequence.</div>` +
            `<button type="button" class="basic-button btn-primary vst-add-clip vst-empty-add" data-vst-add-clip>+ Add a clip</button>` +
            `</div>`;
        wireTopbar();
        return;
    }

    const lastLayout = layouts[layouts.length - 1];
    const endPx = lastLayout.startPx + lastLayout.widthPx;
    const gridTicks = computeRulerTicks(totalSeconds, pxPerSecond).map(
        (t) =>
            `<span class="vst-tick vst-tick-grid" style="left:${t.x}px"><span class="vst-tick-label">${escapeHtml(formatRulerLabel(t.seconds, unit, fps))}</span></span>`,
    );
    const minorStep = chooseRulerStepSeconds(pxPerSecond) / 5;
    const minorTicks: string[] = [];
    const MAX_MINOR_TICKS = 5000;
    for (let i = 1; i <= MAX_MINOR_TICKS; i++) {
        const t = i * minorStep;
        if (t > totalSeconds + 1e-6) {
            break;
        }
        if (i % 5 === 0) {
            continue;
        }
        minorTicks.push(
            `<span class="vst-tick vst-tick-minor" style="left:${t * pxPerSecond}px" aria-hidden="true"></span>`,
        );
    }
    const seamTicks = layouts
        .slice(1)
        .map(
            (l) =>
                `<span class="vst-tick vst-tick-seam" style="left:${l.startPx}px" aria-hidden="true"></span>`,
        );
    const endTick = `<span class="vst-tick vst-tick-end" style="left:${endPx}px"><span class="vst-tick-label">${escapeHtml(formatRulerLabel(totalSeconds, unit, fps))}</span></span>`;
    const ticks: string[] = [
        ...minorTicks,
        ...gridTicks,
        ...seamTicks,
        endTick,
    ];

    const regions = layouts
        .map((l) => {
            const clip = clips[l.index];
            const skipClass = l.skipped ? " vst-region-skipped" : "";
            const tinyClass = l.widthPx <= 12 ? " vst-region-tiny" : "";
            const skipChip = l.skipped
                ? `<span class="vst-chip vst-chip-skip">skipped</span>`
                : "";
            const dur = escapeHtml(
                formatTimeLabel(l.durationSeconds, unit, fps),
            );
            const skipTitle = l.skipped ? "Unskip clip" : "Skip clip";
            const skipGlyph = l.skipped ? "⟲" : "⊘";
            const controls =
                `<div class="vst-region-controls">` +
                `<button type="button" class="vst-region-btn${l.skipped ? " vst-region-btn-active" : ""}" data-vst-region-action="skip" title="${skipTitle}" aria-label="${skipTitle}">${skipGlyph}</button>` +
                `</div>`;
            const rightGrip = lengthDerived(clip)
                ? ""
                : `<div class="vst-region-resize" title="Drag to change clip duration"></div>`;
            const hue = clipHueCss(clip.hue);
            const renderWidth = clipInnerWidth(l.widthPx);
            return (
                `<div class="vst-region${skipClass}${tinyClass}" style="left:${l.startPx}px;width:${renderWidth}px;--clip-hue:${hue}" data-clip-idx="${l.index}" title="Clip ${l.index} · ${dur} · Click to edit · Shift+click to delete">` +
                renderRegionThumb(clip) +
                renderKeyframes(clip, l.index, l.durationSeconds, fps, unit) +
                `<div class="vst-region-head">` +
                `<span class="vst-region-name">Clip ${l.index}</span>` +
                renderStageChips(clip, l.index) +
                `<span class="vst-chip" title="Keyframes">◆ ${l.keyframeCount}</span>` +
                skipChip +
                `<span class="vst-region-dur">${dur}</span>` +
                `</div>` +
                renderBadges(clip, l.index) +
                controls +
                rightGrip +
                `</div>` +
                // Retake mini-lane under the clip region, like the prompt
                // track's relay lane: click empty space to add a retake.
                `<div class="vst-retake-lane" data-vst-retake-add data-clip-idx="${l.index}" style="left:${l.startPx}px;width:${renderWidth}px" title="Click empty space to add a retake window">` +
                renderRetakeOverlay(clip, l.index, l.durationSeconds) +
                `</div>`
            );
        })
        .join("");

    const audioRow = renderAudioTrackRow(clips, layouts);
    const referencesRow = renderReferencesTrackRow(clips, layouts, fps, unit);

    const videoHead = renderTrackHead(
        "vst-track-icon-video",
        "▶",
        "Video",
        headTag("clip", "Clip", { active: true }) +
            headTag("retake", "Retake", {
                active: clips.some((c) => c.retake != null),
            }),
    );

    const promptRow = renderPromptTrackRow(
        clips,
        layouts,
        pxPerSecond,
        `${options?.globalPrompt ?? ""}`,
    );

    const planeWidth = TRACK_HEADER_W_PX + Math.max(totalPx + 160, 320);
    body.innerHTML =
        `${header}<div class="vst-scroll"><div class="vst-plane" style="width:${planeWidth}px">` +
        `<div class="vst-ruler-row">` +
        `<div class="vst-corner">Timeline</div>` +
        `<div class="vst-ruler">${ticks.join("")}</div>` +
        `</div>` +
        promptRow +
        `<div class="vst-track-row vst-track-video">${videoHead}<div class="vst-track-cell">${regions}${renderBoundarySeams(clips, layouts)}</div></div>` +
        referencesRow +
        audioRow +
        `</div></div>`;
    wireTopbar();
    wireScroll();
};
