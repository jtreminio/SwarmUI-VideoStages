import { escapeAttr as escapeHtml } from "./renderUtils";
import {
    chooseRulerStepSeconds,
    computeRulerTicks,
    formatRulerLabel,
    safeFps,
    type TimelineUnit,
} from "./timelineDetail";
import type { TimelineTiming } from "./timelineTiming";
import {
    resolveTimelineTiming,
    timelineDisplaySeconds,
} from "./timelineTiming";
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

const renderRulerTicks = (
    layouts: RegionLayout[],
    totalSeconds: number,
    endPx: number,
    pxPerSecond: number,
    fps: number,
    unit: TimelineUnit,
    timing: TimelineTiming,
): string => {
    const endLabel = formatRulerLabel(totalSeconds, unit, fps);
    const gridTicks = computeRulerTicks(totalSeconds, pxPerSecond)
        .filter(
            (tick) =>
                Math.abs(tick.seconds - totalSeconds) > 1e-6 &&
                !(
                    formatRulerLabel(tick.seconds, unit, fps) === endLabel &&
                    Math.abs(tick.x - endPx) < 40
                ),
        )
        .map(
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
    const seamTicks = timing.boundaries.map((boundary) => {
        const editPoint = layouts[boundary.rightIdx]?.startPx ?? 0;
        return `<span class="vst-tick vst-tick-seam" style="left:${editPoint}px" aria-hidden="true"></span>`;
    });
    const endTick =
        `<span class="vst-tick vst-tick-end" style="left:${endPx}px">` +
        `<span class="vst-tick-label">${escapeHtml(endLabel)}</span>` +
        `</span>`;
    const outputTick =
        timing.outputSeconds < totalSeconds - 1e-6
            ? `<span class="vst-tick vst-tick-output" style="left:${timing.outputSeconds * pxPerSecond}px">` +
              `<span class="vst-tick-label">${escapeHtml(formatRulerLabel(timing.outputSeconds, unit, fps))} output</span>` +
              `</span>`
            : "";
    return [
        ...minorTicks,
        ...gridTicks,
        ...seamTicks,
        outputTick,
        endTick,
    ].join("");
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

    const timing = resolveTimelineTiming(clips, fps, options?.capabilities);
    const layouts = computeRegionLayout(clips, { pxPerSecond, timing });
    const totalSeconds = timelineDisplaySeconds(clips, timing);
    const totalPx = layouts.reduce(
        (max, layout) => Math.max(max, layout.startPx + layout.widthPx),
        0,
    );
    const header = renderTimelineHeader(
        clips.length,
        timing.outputSeconds,
        fps,
        unit,
        pxPerSecond,
        options,
        timing,
    );
    const diagnostics = renderDiagnosticPanel(options?.diagnostics);

    if (clips.length === 0) {
        body.innerHTML =
            `${header}${diagnostics}<div class="vst-empty">` +
            `<div class="vst-empty-icon" aria-hidden="true">🎬</div>` +
            `<div class="vst-empty-title">No clips yet.</div>` +
            `<div class="vst-empty-hint">Use the button below to start building your sequence.</div>` +
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
        timing,
        pxPerSecond,
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
        timing.outputSeconds,
    );
    const planeWidth = TRACK_HEADER_W_PX + Math.max(totalPx + 160, 320);
    body.innerHTML =
        `${header}${diagnostics}<div class="vst-scroll"><div class="vst-plane" style="width:${planeWidth}px">` +
        `<div class="vst-ruler-row">` +
        `<div class="vst-corner">Timeline</div>` +
        `<div class="vst-ruler">${renderRulerTicks(layouts, totalSeconds, totalSeconds * pxPerSecond, pxPerSecond, fps, unit, timing)}</div>` +
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
