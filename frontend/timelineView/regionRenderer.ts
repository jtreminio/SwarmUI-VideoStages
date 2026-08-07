import type { CapabilityViewResolver } from "../architectures/policy";
import { clipHueCss } from "../clipColor";
import { executableBoundaries } from "../clipSemantics";
import { mediaPreviewSrc } from "../constants";
import { escapeHtml } from "../renderUtils";
import { skipGlyph, skipTitle } from "../skipVocabulary";
import {
    formatSecondsTenth,
    formatTimeLabel,
    keyframeTimeSeconds,
    refSourceLabel,
    shortModelName,
    stageChipLabel,
    stageChipTitle,
    type TimelineUnit,
} from "../timelineDetail";
import type { TimelineBoundaryImpact, TimelineTiming } from "../timelineTiming";
import { keyframeLeftPercent, spanGeometry } from "../trackDomUtils";
import type { BoundaryOut, Clip, FrameRefImage } from "../types";
import { roundToTenth } from "../utils";
import type { RegionLayout } from "./layout";
import {
    backgroundImageDataAttr,
    clipInnerWidth,
    headTag,
    laneVisible,
    renderTrackHead,
    renderWindowSpan,
} from "./rendering";

const refFrame = (ref: FrameRefImage): number => Math.max(0, ref.frame ?? 0);

const hasRetake = (clip: Clip): boolean => clip.retake != null;

const retakeLaneVisible = (
    clip: Clip,
    capabilities?: CapabilityViewResolver,
): boolean => laneVisible(clip, "retake", hasRetake(clip), capabilities);

const renderRegionThumb = (clip: Clip): string => {
    const withImage = (clip.frameRefs ?? []).filter(
        (ref) => !!ref.uploadedImage?.data,
    );
    if (withImage.length === 0) {
        return "";
    }
    const startPool = withImage.filter((ref) => ref.fromEnd !== true);
    const startRef = (startPool.length > 0 ? startPool : withImage).reduce(
        (best, ref) => (refFrame(ref) < refFrame(best) ? ref : best),
    );
    let endRef: FrameRefImage | null =
        withImage.find((ref) => ref.fromEnd === true) ?? null;
    if (!endRef) {
        const highest = withImage.reduce((best, ref) =>
            refFrame(ref) > refFrame(best) ? ref : best,
        );
        if (highest !== startRef) {
            endRef = highest;
        }
    }
    const cell = (ref: FrameRefImage, side: "start" | "end"): string => {
        const source = mediaPreviewSrc(ref.uploadedImage?.data ?? "");
        return `<div class="vst-region-thumb-cell vst-region-thumb-${side}"${backgroundImageDataAttr(source)}></div>`;
    };
    const cells = cell(startRef, "start") + (endRef ? cell(endRef, "end") : "");
    return `<div class="vst-region-thumb" data-cells="${endRef ? 2 : 1}" aria-hidden="true">${cells}</div>`;
};

const renderRetakeRegionShade = (
    clip: Clip,
    durationSeconds: number,
): string => {
    const retake = clip.retake;
    if (!retake || durationSeconds <= 0) {
        return "";
    }
    const { left, width, empty } = spanGeometry(
        retake.startSeconds,
        retake.lengthSeconds,
        durationSeconds,
    );
    if (empty) {
        return "";
    }
    return `<div class="vst-region-off" style="left:${left}%;width:${width}%" aria-hidden="true"></div>`;
};

const renderRetakeOverlay = (
    clip: Clip,
    clipIdx: number,
    durationSeconds: number,
    editable: boolean,
): string => {
    const retake = clip.retake;
    if (!retake || durationSeconds <= 0) {
        return "";
    }
    const { startSeconds: start, endSeconds: end } = spanGeometry(
        retake.startSeconds,
        retake.lengthSeconds,
        durationSeconds,
    );
    const label = `RETAKE ${roundToTenth(start)}–${roundToTenth(end)} s`;
    return renderWindowSpan({
        className: "vst-retake",
        dataAttrs: `data-vst-retake data-clip-idx="${clipIdx}"`,
        edgeAttr: "data-vst-retake-edge",
        labelClass: "vst-retake-label",
        label,
        title: editable
            ? `${label} · drag to move/resize · Shift+click to delete`
            : `${label} · unsupported by this architecture · click to inspect or Shift+click to delete`,
        ariaLabel: editable
            ? label
            : `${label}. Unsupported persisted retake; available for inspection or removal.`,
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
    const frameRefs = clip.frameRefs ?? [];
    if (frameRefs.length === 0) {
        return "";
    }
    const markers = frameRefs
        .map((ref, refIdx) => {
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
    return `<div class="vst-keys" title="Frame reference markers">${markers}</div>`;
};

const renderBadges = (clip: Clip, clipIdx: number): string => {
    const firstStage = (clip.stages ?? [])[0];
    if (!firstStage) {
        return `<div class="vst-badges"></div>`;
    }
    const model = firstStage.model ?? "";
    const title = `Clip model: ${`${model}`.trim() || "(default)"} — click to change (applies to Stage 0)`;
    const modelBadge =
        `<span class="vst-badge vst-badge-model" data-vst-model data-clip-idx="${clipIdx}" role="button" tabindex="0" title="${escapeHtml(title)}" aria-label="${escapeHtml(title)}">` +
        `${escapeHtml(shortModelName(model))}</span>`;
    const icLoraCount = (clip.icLoras ?? []).length;
    const icLoraTitle = `${icLoraCount} IC-LoRA${icLoraCount === 1 ? "" : "s"} on this clip — edit in the clip panel`;
    const icLoraBadge =
        icLoraCount > 0
            ? `<span class="vst-badge vst-badge-iclora" title="${escapeHtml(icLoraTitle)}" aria-label="${escapeHtml(icLoraTitle)}">IC×${icLoraCount}</span>`
            : "";
    return `<div class="vst-badges">${modelBadge}${icLoraBadge}</div>`;
};

const renderStageChips = (clip: Clip, clipIdx: number): string =>
    (clip.stages ?? [])
        .map((stage, stageIdx) => {
            const skipped = stage?.skipped === true;
            const skippedClass = skipped ? " vst-stage-chip-skipped" : "";
            const title = `${stageChipTitle(stage, stageIdx)}${skipped ? " (skipped)" : ""} · click to edit${stageIdx === 0 ? "" : " · Shift+click to delete"}`;
            const label = `${skipped ? "⊘ " : ""}${stageChipLabel(stageIdx)}`;
            return (
                `<span class="vst-chip vst-stage-chip${skippedClass}" data-vst-stage data-clip-idx="${clipIdx}" data-stage-idx="${stageIdx}" role="button" tabindex="0" title="${escapeHtml(title)}">` +
                `${escapeHtml(label)}</span>`
            );
        })
        .join("");

const lengthDerived = (clip: Clip): boolean =>
    clip.clipLengthFromAudio === true || clip.clipLengthFromControlNet === true;

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
 * One chip per EXECUTABLE join (`executableBoundaries`), positioned at the start
 * of the clip execution actually continues into — so a skipped clip between two
 * active ones shows the single A→C seam the backend compiles, not two.
 */
export const renderBoundarySeams = (
    clips: Clip[],
    layouts: RegionLayout[],
    capabilities?: CapabilityViewResolver,
    timing?: TimelineTiming,
    pxPerSecond = 1,
): string =>
    executableBoundaries(clips)
        .flatMap((seam) => {
            const layout = layouts[seam.rightIdx];
            if (!layout) {
                return [];
            }
            const clip = clips[seam.leftIdx];
            const value: BoundaryOut = clip.boundaryOut ?? "cut";
            const capability = capabilities?.forBoundaryIndex(
                clips,
                seam.leftIdx,
            );
            const policyEffective = capability?.effective(value) ?? value;
            const impact = timing?.boundaries.find(
                (boundary) => boundary.leftIdx === seam.leftIdx,
            );
            const effective = impact?.effectiveMode ?? policyEffective;
            const glyph = BOUNDARY_GLYPH[effective] ?? BOUNDARY_GLYPH.cut;
            const label = BOUNDARY_LABEL[value] ?? BOUNDARY_LABEL.cut;
            const effectiveLabel = BOUNDARY_LABEL[effective];
            const fallback =
                value === effective
                    ? ""
                    : ` Requested ${label}; effective ${effectiveLabel}.`;
            const shared =
                impact && impact.overlapFrames > 0
                    ? ` ${impact.overlapFrames} frames (${formatSecondsTenth(impact.overlapSeconds)}) shared.`
                    : "";
            const duration =
                impact && impact.overlapFrames > 0
                    ? formatSecondsTenth(impact.overlapSeconds)
                    : "";
            const sharedPixels =
                impact && impact.overlapFrames > 0
                    ? impact.overlapSeconds * pxPerSecond
                    : 0;
            const density =
                sharedPixels >= 88
                    ? "full"
                    : sharedPixels >= 44
                      ? "compact"
                      : "icon";
            const title = `Boundary clip ${seam.leftIdx} → ${seam.rightIdx}: ${label}.${fallback}${shared} Click to edit.`;
            const ariaLabel = `Clip ${seam.leftIdx} outgoing boundary: ${label}.${fallback}${shared} Click to edit.`;
            const left = layout.startPx;
            return [
                `<button type="button" class="basic-button vst-boundary-chip vst-boundary-chip-${density} vst-boundary-${effective}${value === effective ? "" : " vst-boundary-fallback"}" data-vst-boundary-chip data-vst-boundary-density="${density}" data-vst-boundary-has-duration="${duration ? "true" : "false"}" data-left-clip-idx="${seam.leftIdx}" data-right-clip-idx="${seam.rightIdx}" data-boundary="${value}" data-effective-boundary="${effective}" style="left:${left}px" title="${escapeHtml(title)}" aria-label="${escapeHtml(ariaLabel)}">` +
                    `<span class="vst-boundary-glyph" aria-hidden="true">${escapeHtml(glyph)}</span>` +
                    (duration
                        ? `<span class="vst-boundary-kind">${escapeHtml(effectiveLabel)}</span>` +
                          `<span class="vst-boundary-divider" aria-hidden="true"></span>` +
                          `<span class="vst-boundary-duration">${escapeHtml(duration)}</span>`
                        : "") +
                    `</button>`,
            ];
        })
        .join("");

const renderBoundaryOverlapBands = (
    layouts: RegionLayout[],
    boundaries: readonly TimelineBoundaryImpact[],
    pxPerSecond: number,
): string =>
    boundaries
        .filter((boundary) => boundary.overlapFrames > 0)
        .map((boundary) => {
            const right = layouts[boundary.rightIdx];
            if (!right) {
                return "";
            }
            const width = boundary.overlapSeconds * pxPerSecond;
            const left =
                boundary.effectiveMode === "continue"
                    ? right.startPx - width
                    : right.startPx - width / 2;
            return (
                `<div class="vst-boundary-overlap vst-boundary-overlap-${boundary.effectiveMode}" ` +
                `style="left:${left}px;width:${width}px" aria-hidden="true"></div>`
            );
        })
        .join("");

const renderRegions = (
    clips: Clip[],
    layouts: RegionLayout[],
    fps: number,
    unit: TimelineUnit,
    capabilities?: CapabilityViewResolver,
): string =>
    layouts
        .map((layout) => {
            const clip = clips[layout.index];
            const skippedClass = layout.skipped ? " vst-region-skipped" : "";
            const tinyClass = layout.widthPx <= 12 ? " vst-region-tiny" : "";
            const skippedChip = layout.skipped
                ? `<span class="vst-chip vst-chip-skip">skipped</span>`
                : "";
            const authoredDurationSeconds =
                layout.frameCount > 0
                    ? layout.frameCount / fps
                    : layout.durationSeconds;
            const duration = escapeHtml(
                unit === "frames" && layout.frameCount > 0
                    ? `${layout.frameCount}f`
                    : formatTimeLabel(authoredDurationSeconds, unit, fps),
            );
            const sharedAllocation = layout.timelineReductionSeconds;
            const timingTitle =
                layout.incomingHandleSeconds > 0
                    ? ` · ${formatSecondsTenth(layout.generatedDurationSeconds)} generated · ${formatSecondsTenth(layout.timelineDurationSeconds)} timeline · ${formatSecondsTenth(layout.incomingHandleSeconds)} handle`
                    : sharedAllocation > 0
                      ? ` · ${formatSecondsTenth(layout.generatedDurationSeconds)} generated · ${formatSecondsTenth(layout.timelineDurationSeconds)} unique · ${formatSecondsTenth(sharedAllocation)} shared`
                      : ` · ${duration}`;
            const skipLabel = skipTitle("clip", layout.skipped);
            const skipMark = skipGlyph(layout.skipped);
            const firstClip = layout.index === 0;
            const controls = firstClip
                ? ""
                : `<div class="vst-region-controls">` +
                  `<button type="button" class="vst-region-btn${layout.skipped ? " vst-region-btn-active" : ""}" data-vst-region-action="skip" title="${skipLabel}" aria-label="${skipLabel}">${skipMark}</button>` +
                  `</div>`;
            const resizeGrip = lengthDerived(clip)
                ? ""
                : `<div class="vst-region-resize" title="Drag to change clip duration"></div>`;
            const width = clipInnerWidth(layout.widthPx);
            const retakeDecision = capabilities
                ?.forClip(clip)
                .decision("retake");
            const retakeSupported = retakeDecision?.supported ?? true;
            const canAddRetake = retakeSupported && !clip.retake;
            const retakeLaneAttrs = canAddRetake
                ? " data-vst-retake-add"
                : retakeSupported
                  ? " data-vst-retake-full"
                  : ' data-vst-capability-disabled="retake"';
            // The policy already says WHY a lane is closed — an architecture
            // that lacks retakes reads differently from a clip still missing
            // its init video, so quote it rather than guess.
            const retakeLaneTitle = canAddRetake
                ? "Click empty space to add a retake window"
                : retakeSupported
                  ? "This clip already has a retake window"
                  : (retakeDecision?.reason ??
                    "Retake is unavailable for this clip");
            return (
                `<div class="vst-region${skippedClass}${tinyClass}" style="left:${layout.startPx}px;width:${width}px;--clip-hue:${clipHueCss(clip.hue)}" data-clip-idx="${layout.index}" data-vst-join-trim-seconds="${sharedAllocation}" title="Clip ${layout.index}${timingTitle} · Click to edit${firstClip ? "" : " · Shift+click to delete"}">` +
                renderRegionThumb(clip) +
                renderRetakeRegionShade(clip, layout.durationSeconds) +
                renderKeyframes(
                    clip,
                    layout.index,
                    authoredDurationSeconds,
                    fps,
                    unit,
                ) +
                `<div class="vst-region-head">` +
                `<span class="vst-region-name">Clip ${layout.index}</span>` +
                renderStageChips(clip, layout.index) +
                `<span class="vst-chip" title="Keyframes">◆ ${layout.keyframeCount}</span>` +
                skippedChip +
                `<span class="vst-region-dur">${duration}</span>` +
                `</div>` +
                renderBadges(clip, layout.index) +
                controls +
                resizeGrip +
                `</div>` +
                (retakeLaneVisible(clip, capabilities)
                    ? `<div class="vst-retake-lane${retakeSupported ? "" : " vst-capability-disabled"}"${retakeLaneAttrs}${retakeSupported || clip.retake ? "" : ' aria-disabled="true"'} data-clip-idx="${layout.index}" style="left:${layout.startPx}px;width:${width}px" title="${escapeHtml(retakeLaneTitle)}">` +
                      renderRetakeOverlay(
                          clip,
                          layout.index,
                          layout.durationSeconds,
                          retakeSupported,
                      ) +
                      `</div>`
                    : "")
            );
        })
        .join("");

export const renderVideoTrackRow = (
    clips: Clip[],
    layouts: RegionLayout[],
    fps: number,
    unit: TimelineUnit,
    capabilities?: CapabilityViewResolver,
    timing?: TimelineTiming,
    pxPerSecond = 1,
): string => {
    const retakeTrack = clips.some((clip) =>
        retakeLaneVisible(clip, capabilities),
    );
    const head = renderTrackHead(
        "vst-track-icon-video",
        "▶",
        "Video",
        headTag("clip", "Clip", { active: true }) +
            (retakeTrack
                ? headTag("retake", "Retake", {
                      active: clips.some(hasRetake),
                  })
                : ""),
    );
    return (
        `<div class="vst-track-row vst-track-video${retakeTrack ? "" : " vst-no-retake"}">${head}<div class="vst-track-cell">` +
        renderRegions(clips, layouts, fps, unit, capabilities) +
        renderBoundaryOverlapBands(
            layouts,
            timing?.outputGeometryAvailable === true ? timing.boundaries : [],
            pxPerSecond,
        ) +
        renderBoundarySeams(clips, layouts, capabilities, timing, pxPerSecond) +
        `</div></div>`
    );
};
