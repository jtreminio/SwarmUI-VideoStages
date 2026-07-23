import type { Clip } from "../types";

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

/** Stable per-segment waveform seed: unique across practical clip/segment indices. */
export const audioSegmentWaveBarHeights = (
    clipIdx: number,
    segmentIdx: number,
    count: number,
): number[] => waveBarHeights(clipIdx * 4099 + segmentIdx + 1, count);

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
    const effectiveOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, (effectiveOffsetX + scrollLeft - headerW) / pxPerSecond);
};

export const zoomAnchorScrollLeft = (
    time: number,
    pxPerSecond: number,
    offsetX: number,
    headerW = TRACK_HEADER_W_PX,
): number => {
    const effectiveOffsetX = Math.max(offsetX, headerW);
    return Math.max(0, headerW + time * pxPerSecond - effectiveOffsetX);
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
    const layouts: RegionLayout[] = [];
    let cursorSeconds = 0;
    let cursorPx = 0;
    for (let index = 0; index < clips.length; index++) {
        const clip = clips[index];
        const durationSeconds = Math.max(0, clip.duration || 0);
        const widthPx = Math.max(
            DEFAULT_MIN_WIDTH_PX,
            durationSeconds * pxPerSecond,
        );
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
