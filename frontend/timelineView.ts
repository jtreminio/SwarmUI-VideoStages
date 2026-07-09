import { clipHueCss } from "./clipColor";
import { clamp, mediaPreviewSrc } from "./constants";
import { matchPresetKey } from "./dimensionPresets";
import {
    audioSourceBadge,
    type Badge,
    chooseRulerStepSeconds,
    computeRulerTicks,
    escapeHtml,
    formatRulerLabel,
    formatTimeLabel,
    keyframeLeftPercent,
    keyframeTimeSeconds,
    refSourceLabel,
    safeFps,
    type TimelineUnit,
} from "./timelineDetail";
import type { Clip, PromptWindow, RefImage } from "./types";

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
    minWidthPx?: number;
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
    const minWidthPx = options?.minWidthPx ?? DEFAULT_MIN_WIDTH_PX;
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

const badgeHtml = (badge: Badge, extraClass = ""): string =>
    `<span class="vst-badge${extraClass}" title="${escapeHtml(badge.title)}">${escapeHtml(badge.label)}</span>`;

const renderRegionThumb = (clip: Clip): string => {
    for (const ref of clip.refs ?? []) {
        const value = ref.uploadedImage?.data;
        if (value) {
            const src = mediaPreviewSrc(value);
            return `<div class="vst-region-thumb" style="background-image:url('${escapeHtml(src)}')" aria-hidden="true"></div>`;
        }
    }
    return "";
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
            const title = `${source} · frame ${ref.frame ?? 0}${isEnd ? " (from end)" : ""}${isPrimary ? " (cover)" : ""} · ${formatTimeLabel(time, unit, fps)} · drag to move, shift-click to toggle from-end`;
            const kindClass =
                (isEnd ? " vst-key-end" : " vst-key-start") +
                (isPrimary ? " vst-key-primary" : "");
            const markAria = `Reference ${refIdx} marker (${source}${isEnd ? ", from end" : ""}) — drag to move, Enter toggles from end`;
            return (
                `<span class="vst-key${kindClass}" data-clip-idx="${clipIdx}" data-ref-idx="${refIdx}" style="left:${left}%" title="${escapeHtml(title)}" role="button" tabindex="0" aria-label="${escapeHtml(markAria)}">` +
                `<span class="vst-key-dot" aria-hidden="true"></span>` +
                `</span>`
            );
        })
        .join("");
    return `<div class="vst-keys" title="Reference markers">${pips}</div>`;
};

const renderBadges = (clip: Clip): string => {
    const badges: string[] = [
        badgeHtml(audioSourceBadge(clip.audioSource ?? ""), " vst-badge-audio"),
    ];
    return `<div class="vst-badges">${badges.join("")}</div>`;
};

const lengthDerived = (clip: Clip): boolean =>
    clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true;

export interface PromptWindowGeom {
    startSec: number;
    endSec: number;
    leftPx: number;
    widthPx: number;
    active: boolean;
}

export const promptWindowGeom = (
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
        active: !window.skipped && `${window.prompt ?? ""}`.trim() !== "",
    };
};

const PROMPT_PLACEHOLDER = "(no prompt)";

const truncatePrompt = (text: string, max = 120): string =>
    text.length > max ? `${text.slice(0, max - 1)}…` : text;

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
        const clipWidth = Math.max(1, layout.widthPx - 2);
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
            major === "" ? PROMPT_PLACEHOLDER : truncatePrompt(major);
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
                const skippedClass = w.skipped ? " vst-minor-skipped" : "";
                const text = `${w.prompt ?? ""}`.trim();
                const label =
                    text === "" ? "(empty)" : truncatePrompt(text, 60);
                return (
                    `<div class="vst-minor-seg${skippedClass}" data-vst-prompt="minor" data-clip-idx="${i}" data-window-idx="${j}" style="left:${g.leftPx}px;width:${g.widthPx}px" title="${escapeHtml(text || "(empty minor prompt)")}">` +
                    `<span class="vst-minor-resize vst-minor-resize-l" data-vst-minor-edge="left" aria-hidden="true"></span>` +
                    `<span class="vst-minor-text">${escapeHtml(label)}</span>` +
                    `<span class="vst-minor-actions">` +
                    `<button type="button" class="vst-minor-act" data-vst-minor-action="skip" title="${w.skipped ? "Enable this minor prompt" : "Skip this minor prompt"}" aria-label="${w.skipped ? "Enable minor prompt" : "Skip minor prompt"}">${w.skipped ? "○" : "◉"}</button>` +
                    `<button type="button" class="vst-minor-act" data-vst-minor-action="delete" title="Delete this minor prompt" aria-label="Delete minor prompt">×</button>` +
                    `</span>` +
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
        `<div class="vst-track-head">` +
        `<div class="vst-track-icon vst-track-icon-prompt" aria-hidden="true">✎</div>` +
        `<div class="vst-track-label"><strong>Prompt</strong><small>major · relay</small></div>` +
        `</div>` +
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
            const nativeClass = native ? " vst-audio-native" : "";
            const width = Math.max(1, l.widthPx - 2);
            const upload =
                !native && clip.audioSource === "Upload"
                    ? clip.uploadedAudio?.fileName
                    : null;
            const labelText = upload
                ? `${badge.label} · ${upload}`
                : badge.label;
            const title = native
                ? "Audio: Native — click to choose an audio source"
                : `${badge.title} — click to edit`;
            const body = native
                ? `<span class="vst-audio-hint" aria-hidden="true">click to add audio</span>`
                : (() => {
                      const barCount = Math.min(
                          400,
                          Math.max(8, Math.floor(width / 5.5)),
                      );
                      const bars = waveBarHeights(l.index, barCount)
                          .map((h) => `<span style="height:${h}%"></span>`)
                          .join("");
                      return `<div class="vst-audio-wave" aria-hidden="true">${bars}</div>`;
                  })();
            return (
                `<div class="vst-audio-clip${nativeClass}" data-vst-audio="clip" data-clip-idx="${l.index}" role="button" tabindex="0" style="left:${l.startPx}px;width:${width}px" title="${escapeHtml(title)}" aria-label="Edit audio for clip ${l.index}">` +
                `<span class="vst-audio-label">${escapeHtml(labelText)}</span>` +
                audioFlagChips(clip) +
                body +
                `</div>`
            );
        })
        .join("");
    return (
        `<div class="vst-track-row vst-track-audio">` +
        `<div class="vst-track-head">` +
        `<div class="vst-track-icon vst-track-icon-audio" aria-hidden="true">♪</div>` +
        `<div class="vst-track-label"><strong>Audio</strong><small>A1 · per-clip</small></div>` +
        `</div>` +
        `<div class="vst-track-cell">${segments}</div>` +
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
            const laneWidth = Math.max(1, l.widthPx - 2);
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
                        ` · click to edit, drag to move`;
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
        `<div class="vst-track-head">` +
        `<div class="vst-track-icon vst-track-icon-refs" aria-hidden="true">⧉</div>` +
        `<div class="vst-track-label"><strong>References</strong><small>image refs</small></div>` +
        `</div>` +
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
        `<button type="button" class="vst-settings-chip" data-vst-settings title="${escapeHtml(settingsTip)}" aria-label="${escapeHtml(settingsTip)}">` +
        `<span class="vst-settings-dims">${chipWidth}×${chipHeight}</span>` +
        `<span class="vst-settings-chip-sep" aria-hidden="true">·</span>` +
        `<span class="vst-settings-fps">${chipFps} fps</span>` +
        `</button>`;

    const enabled = options?.enabled !== false;
    const enableToggle =
        `<label class="vst-enable" title="Enable VideoStages. While off, none of this timeline is sent to the backend — a normal image/video generates as usual.">` +
        `<input type="checkbox" class="vst-enable-input" role="switch" data-vst-enable${enabled ? " checked" : ""}>` +
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
        `<button type="button" class="vst-toggle vst-add-clip" data-vst-add-clip title="Add a new clip to the end of the sequence">+ Clip</button>` +
        `<span class="vst-tool-sep" aria-hidden="true"></span>` +
        `<div class="vst-zoom" role="group" aria-label="Timeline zoom (Ctrl+wheel over the track)">` +
        `<button type="button" class="vst-toggle vst-zoom-btn" data-vst-zoom-out title="Zoom out (show more time)" aria-label="Zoom out">−</button>` +
        `<span class="vst-zoom-pct" data-vst-zoom-pct title="Zoom level (100% = default)">${zoomPct}%</span>` +
        `<input type="range" class="vst-zoom-slider" data-vst-zoom-slider min="${MIN_PX_PER_SECOND}" max="${MAX_PX_PER_SECOND}" step="1" value="${Math.round(pxPerSecond)}" aria-label="Zoom (pixels per second)" title="Zoom (applies on release)">` +
        `<button type="button" class="vst-toggle vst-zoom-btn" data-vst-zoom-in title="Zoom in (show less time, more detail)" aria-label="Zoom in">+</button>` +
        `<button type="button" class="vst-toggle vst-zoom-btn" data-vst-zoom-fit title="Fit the whole sequence to the view" aria-label="Zoom to fit">Fit</button>` +
        `</div>` +
        `<span class="vst-tool-sep" aria-hidden="true"></span>` +
        `<button type="button" class="vst-toggle vst-toggle-unit" data-vst-unit-toggle title="Toggle ruler units between seconds and frames (in-memory only)">${toggleLabel}</button>` +
        `<button type="button" class="vst-toggle vst-hist-btn" data-vst-undo title="Undo (Ctrl+Z)" aria-label="Undo">↶</button>` +
        `<button type="button" class="vst-toggle vst-hist-btn" data-vst-redo title="Redo (Ctrl+Shift+Z or Ctrl+Y)" aria-label="Redo">↷</button>` +
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
            `<button type="button" class="vst-toggle vst-add-clip vst-empty-add" data-vst-add-clip>+ Add a clip</button>` +
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
                `<button type="button" class="vst-region-btn vst-region-btn-delete" data-vst-region-action="delete" title="Delete clip" aria-label="Delete clip">✕</button>` +
                `</div>`;
            const rightGrip = lengthDerived(clip)
                ? ""
                : `<div class="vst-region-resize" title="Drag to change clip duration"></div>`;
            const hue = clipHueCss(clip.hue);
            const skippedStages = (clip.stages ?? []).filter(
                (stage) => stage?.skipped,
            ).length;
            const stagesTitle =
                skippedStages > 0
                    ? `Stages: ${l.stageCount} (${skippedStages} skipped)`
                    : "Stages";
            const renderWidth = Math.max(1, l.widthPx - 2);
            return (
                `<div class="vst-region${skipClass}${tinyClass}" style="left:${l.startPx}px;width:${renderWidth}px;--clip-hue:${hue}" data-clip-idx="${l.index}" title="Clip ${l.index} · ${dur}">` +
                renderRegionThumb(clip) +
                renderKeyframes(clip, l.index, l.durationSeconds, fps, unit) +
                `<div class="vst-region-head">` +
                `<span class="vst-region-name">Clip ${l.index}</span>` +
                `<span class="vst-chip" title="${escapeHtml(stagesTitle)}">▤ ${l.stageCount}</span>` +
                `<span class="vst-chip" title="Keyframes">◆ ${l.keyframeCount}</span>` +
                skipChip +
                `<span class="vst-region-dur">${dur}</span>` +
                `</div>` +
                renderBadges(clip) +
                controls +
                rightGrip +
                `</div>`
            );
        })
        .join("");

    const audioRow = renderAudioTrackRow(clips, layouts);
    const referencesRow = renderReferencesTrackRow(clips, layouts, fps, unit);

    const videoHead =
        `<div class="vst-track-head">` +
        `<div class="vst-track-icon vst-track-icon-video" aria-hidden="true">▶</div>` +
        `<div class="vst-track-label"><strong>Video</strong><small>V1 · ${clips.length} ${clipWord}</small></div>` +
        `</div>`;

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
        `<div class="vst-track-row vst-track-video">${videoHead}<div class="vst-track-cell">${regions}</div></div>` +
        referencesRow +
        audioRow +
        `</div></div>`;
    wireTopbar();
    wireScroll();
};
