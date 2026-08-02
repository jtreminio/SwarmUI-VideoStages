import type { CapabilityViewResolver } from "../architectures/policy";
import type { AuthoringDiagnostic } from "../authoringDiagnostics";
import { matchAspectRatio } from "../dimensionPresets";
import { escapeAttr as escapeHtml } from "../renderUtils";
import { formatSecondsTenth, type TimelineUnit } from "../timelineDetail";
import type { TimelineTiming } from "../timelineTiming";
import type { AudioTrack } from "../types";
import {
    DEFAULT_PX_PER_SECOND,
    MAX_PX_PER_SECOND,
    MIN_PX_PER_SECOND,
    ZOOM_FACTOR,
} from "./layout";

export interface RenderTimelineOptions {
    fps?: number;
    width?: number;
    height?: number;
    dimsExplicit?: boolean;
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
    audioTracks?: AudioTrack[];
    diagnostics?: readonly AuthoringDiagnostic[];
    capabilities?: CapabilityViewResolver;
}

export const renderDiagnosticPanel = (
    diagnostics: readonly AuthoringDiagnostic[] = [],
): string => {
    const content = diagnostics
        .map(
            (item) =>
                `<div class="vst-diagnostic vst-diagnostic-${item.severity}" data-vst-diagnostic="${escapeHtml(item.code)}">` +
                `${item.clipIdx === undefined ? "" : `<strong>Clip ${item.clipIdx}:</strong> `}` +
                `${escapeHtml(item.message)}</div>`,
        )
        .join("");
    return content
        ? `<div class="vst-diagnostics" role="status">${content}</div>`
        : "";
};

export const renderTimelineHeader = (
    clipCount: number,
    totalSeconds: number,
    fps: number,
    unit: TimelineUnit,
    pxPerSecond: number,
    options?: RenderTimelineOptions,
    timing?: TimelineTiming,
): string => {
    const toggleLabel = unit === "frames" ? "Show seconds" : "Show frames";
    const clipWord = `clip${clipCount === 1 ? "" : "s"}`;
    const totalLabel =
        unit === "frames"
            ? `${timing?.outputFrames ?? Math.round(totalSeconds * fps)}f`
            : formatSecondsTenth(totalSeconds);
    const zoomPct = Math.round((pxPerSecond / DEFAULT_PX_PER_SECOND) * 100);
    const rawSelected = options?.selectedIndex;
    const selectedIndex =
        typeof rawSelected === "number" &&
        Number.isInteger(rawSelected) &&
        rawSelected >= 0 &&
        rawSelected < clipCount
            ? rawSelected
            : null;
    const selectedHidden = selectedIndex === null ? " hidden" : "";
    const joinFrames = timing?.joinFrames ?? 0;
    const joinSeconds = timing?.joinSeconds ?? 0;
    const handleSeconds =
        timing?.boundaries.reduce(
            (sum, boundary) => sum + boundary.handleSeconds,
            0,
        ) ?? 0;
    const joinFrameLabel = `${joinFrames > 0 ? "−" : ""}${joinFrames}f`;
    const joinSecondsLabel = `${joinSeconds > 0 ? "−" : ""}${formatSecondsTenth(joinSeconds)}`;
    const authoredLabel = formatSecondsTenth(
        timing?.authoredSeconds ?? totalSeconds,
    );
    const secondary =
        unit === "frames"
            ? `${timing?.generatedFrames ?? 0}f generated · ${joinFrameLabel} shared`
            : handleSeconds > 0
              ? `${authoredLabel} authored · +${formatSecondsTenth(handleSeconds)} handle · ${joinSecondsLabel} shared`
              : `${authoredLabel} authored · ${joinSecondsLabel} joins`;
    const readout =
        `<span class="vst-readout" data-vst-readout>` +
        `<span class="vst-readout-output" title="Published sequence length">${escapeHtml(totalLabel)} output</span>` +
        `<span class="vst-readout-detail" title="Authored length and resolved shared joins">${escapeHtml(secondary)}</span>` +
        `<span class="vst-dot" data-vst-readout-sel-dot${selectedHidden}>·</span>` +
        `<span class="vst-readout-sel" data-vst-readout-sel title="Selected clip"${selectedHidden}>${selectedIndex !== null ? `clip ${selectedIndex}` : ""}</span>` +
        `</span>`;

    const width = Math.max(0, Math.round(options?.width ?? 0));
    const height = Math.max(0, Math.round(options?.height ?? 0));
    const dimsExplicit = options?.dimsExplicit === true;
    const ratioId =
        dimsExplicit && width > 0 && height > 0
            ? matchAspectRatio(width, height)
            : null;
    const dimsSource = dimsExplicit
        ? ratioId
            ? `${ratioId} aspect ratio`
            : "custom"
        : "inherited from image resolution";
    const fpsSource = "synced with Video FPS";
    const settingsTip = `Resolution: ${dimsSource}; FPS: ${fpsSource}. Click to edit.`;
    const settingsChip =
        `<button type="button" class="basic-button small-button vst-settings-chip" data-vst-settings title="${escapeHtml(settingsTip)}" aria-label="${escapeHtml(settingsTip)}">` +
        `<span class="vst-settings-dims">${width}×${height}</span>` +
        `<span class="vst-settings-chip-sep" aria-hidden="true">·</span>` +
        `<span class="vst-settings-fps">${fps} fps</span>` +
        `</button>`;

    const enabled = options?.enabled !== false;
    const enableToggle =
        `<label class="vst-enable" title="Enable VideoStages. While off, none of this timeline is sent to the backend — a normal image/video generates as usual.">` +
        `<span class="toggle-switch">` +
        `<input type="checkbox" class="auto-slider-toggle vst-enable-input" role="switch" data-vst-enable${enabled ? " checked" : ""}>` +
        `<div class="auto-slider-toggle-content"></div>` +
        `</span>` +
        `<span class="vst-enable-label">Enable</span>` +
        `</label>`;

    return (
        `<div class="vst-topbar${enabled ? "" : " vst-topbar-disabled"}">` +
        `<div class="vst-topbar-main">` +
        `<span class="vst-title">Timeline</span>` +
        enableToggle +
        `<span class="vst-sub"><span class="vst-stat-num">${clipCount}</span> ${clipWord}</span>` +
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
        `</div>`
    );
};

export const wireTimelineToolbar = (
    body: HTMLElement,
    options?: RenderTimelineOptions,
): void => {
    const wire = (
        selector: string,
        handler: (() => void) | undefined,
    ): void => {
        if (!handler) {
            return;
        }
        body.querySelector(selector)?.addEventListener("click", () =>
            handler(),
        );
    };

    const enableInput =
        body.querySelector<HTMLInputElement>("[data-vst-enable]");
    if (enableInput && options?.onToggleEnabled) {
        enableInput.addEventListener("change", () => {
            options.onToggleEnabled?.(enableInput.checked);
        });
    }
    const settingsButton = body.querySelector<HTMLElement>(
        "[data-vst-settings]",
    );
    if (settingsButton && options?.onOpenSettings) {
        settingsButton.addEventListener("click", () => {
            options.onOpenSettings?.(settingsButton);
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
            const percent = body.querySelector<HTMLElement>(
                "[data-vst-zoom-pct]",
            );
            if (percent) {
                const value = Number.parseFloat(slider.value);
                percent.textContent = `${Math.round((value / DEFAULT_PX_PER_SECOND) * 100)}%`;
            }
        });
        if (options?.onZoomSlider) {
            slider.addEventListener("change", () => {
                options.onZoomSlider?.(Number.parseFloat(slider.value));
            });
        }
    }
    if (options?.onAddClip) {
        for (const button of body.querySelectorAll("[data-vst-add-clip]")) {
            button.addEventListener("click", () => options.onAddClip?.());
        }
    }
};

export const wireTimelineZoomWheel = (
    body: HTMLElement,
    options?: RenderTimelineOptions,
): void => {
    const onZoomWheel = options?.onZoomWheel;
    if (!onZoomWheel) {
        return;
    }
    body.querySelector<HTMLElement>(".vst-scroll")?.addEventListener(
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
