import {
    chooseRulerStepSeconds,
    computeRulerTicks,
    escapeHtml,
    formatRulerLabel,
    safeFps,
    type TimelineUnit,
} from "./timelineDetail";
import {
    clampPxPerSecond,
    computeRegionLayout,
    DEFAULT_PX_PER_SECOND,
    type RegionLayout,
    TRACK_HEADER_W_PX,
} from "./timelineView/layout";
import { renderVideoTrackRow } from "./timelineView/regionRenderer";
import { applyBackgroundImages } from "./timelineView/rendering";
import {
    type RenderTimelineOptions,
    renderDiagnosticPanel,
    renderTimelineHeader,
    wireTimelineToolbar,
    wireTimelineZoomWheel,
} from "./timelineView/toolbar";
import {
    renderAudioTrackRow,
    renderPromptTrackRow,
    renderReferencesTrackRow,
} from "./timelineView/trackRows";
import type { Clip } from "./types";

export {
    clampPxPerSecond,
    computeFitPxPerSecond,
    DEFAULT_PX_PER_SECOND,
    TRACK_HEADER_W_PX,
    ZOOM_FACTOR,
    zoomAnchorScrollLeft,
    zoomAnchorTime,
} from "./timelineView/layout";
export { BOUNDARY_GLYPH, BOUNDARY_LABEL } from "./timelineView/regionRenderer";

const renderRulerTicks = (
    layouts: RegionLayout[],
    totalSeconds: number,
    pxPerSecond: number,
    fps: number,
    unit: TimelineUnit,
): string => {
    const lastLayout = layouts[layouts.length - 1];
    const endPx = lastLayout.startPx + lastLayout.widthPx;
    const gridTicks = computeRulerTicks(totalSeconds, pxPerSecond).map(
        (tick) =>
            `<span class="vst-tick vst-tick-grid" style="left:${tick.x}px"><span class="vst-tick-label">${escapeHtml(formatRulerLabel(tick.seconds, unit, fps))}</span></span>`,
    );
    const minorStep = chooseRulerStepSeconds(pxPerSecond) / 5;
    const minorTicks: string[] = [];
    const maxMinorTicks = 5000;
    for (let i = 1; i <= maxMinorTicks; i++) {
        const seconds = i * minorStep;
        if (seconds > totalSeconds + 1e-6) {
            break;
        }
        if (i % 5 === 0) {
            continue;
        }
        minorTicks.push(
            `<span class="vst-tick vst-tick-minor" style="left:${seconds * pxPerSecond}px" aria-hidden="true"></span>`,
        );
    }
    const seamTicks = layouts
        .slice(1)
        .map(
            (layout) =>
                `<span class="vst-tick vst-tick-seam" style="left:${layout.startPx}px" aria-hidden="true"></span>`,
        );
    const endTick =
        `<span class="vst-tick vst-tick-end" style="left:${endPx}px">` +
        `<span class="vst-tick-label">${escapeHtml(formatRulerLabel(totalSeconds, unit, fps))}</span>` +
        `</span>`;
    return [...minorTicks, ...gridTicks, ...seamTicks, endTick].join("");
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
    const totalSeconds = layouts.reduce(
        (sum, layout) => sum + layout.durationSeconds,
        0,
    );
    const totalPx = layouts.reduce(
        (max, layout) => Math.max(max, layout.startPx + layout.widthPx),
        0,
    );
    const header = renderTimelineHeader(
        clips.length,
        totalSeconds,
        fps,
        unit,
        pxPerSecond,
        options,
    );
    const diagnostics = renderDiagnosticPanel(options?.diagnostics);

    if (clips.length === 0) {
        body.innerHTML =
            `${header}${diagnostics}<div class="vst-empty">` +
            `<div class="vst-empty-icon" aria-hidden="true">🎬</div>` +
            `<div class="vst-empty-title">No clips yet.</div>` +
            `<div class="vst-empty-hint">Add one here — or in the VideoStages panel on the left — to start building your sequence.</div>` +
            `<button type="button" class="basic-button btn-primary vst-add-clip vst-empty-add" data-vst-add-clip>+ Add a clip</button>` +
            `</div>`;
        wireTimelineToolbar(body, options);
        return;
    }

    const promptRow = renderPromptTrackRow(
        clips,
        layouts,
        pxPerSecond,
        `${options?.globalPrompt ?? ""}`,
        options?.capabilities,
    );
    const videoRow = renderVideoTrackRow(
        clips,
        layouts,
        fps,
        unit,
        options?.capabilities,
    );
    const referencesRow = renderReferencesTrackRow(
        clips,
        layouts,
        fps,
        unit,
        options?.capabilities,
    );
    const renderedAudioRow = renderAudioTrackRow(
        clips,
        layouts,
        options?.capabilities,
        options?.audioTracks,
        pxPerSecond,
    );
    const planeWidth = TRACK_HEADER_W_PX + Math.max(totalPx + 160, 320);
    body.innerHTML =
        `${header}${diagnostics}<div class="vst-scroll"><div class="vst-plane" style="width:${planeWidth}px">` +
        `<div class="vst-ruler-row">` +
        `<div class="vst-corner">Timeline</div>` +
        `<div class="vst-ruler">${renderRulerTicks(layouts, totalSeconds, pxPerSecond, fps, unit)}</div>` +
        `</div>` +
        promptRow +
        videoRow +
        referencesRow +
        renderedAudioRow +
        `</div></div>`;
    applyBackgroundImages(body);
    wireTimelineToolbar(body, options);
    wireTimelineZoomWheel(body, options);
};
