import { CLIP_DURATION_MIN, clamp, REF_FRAME_MIN } from "./constants";
import { getKnownReferenceFrameMax } from "./normalizationStage";
import {
    type FrameGridSpec,
    framesForClip,
    snapDurationToFps,
} from "./renderUtils";
import type { Clip, RootDefaults } from "./types";

export const pxToDuration = (
    px: number,
    pxPerSecond: number,
    fps: number,
): number => {
    if (
        !Number.isFinite(px) ||
        !Number.isFinite(pxPerSecond) ||
        pxPerSecond <= 0
    ) {
        return CLIP_DURATION_MIN;
    }
    const seconds = Math.max(CLIP_DURATION_MIN, px / pxPerSecond);
    return Math.max(CLIP_DURATION_MIN, snapDurationToFps(seconds, fps));
};

export const pxToFrame = (
    pointerXWithinRegion: number,
    regionWidthPx: number,
    durationSeconds: number,
    fps: number,
    fromEnd: boolean,
    frameGrid: FrameGridSpec,
): number => {
    const safeFps = Number.isFinite(fps) && fps > 0 ? fps : 1;
    const authoredDuration =
        Number.isFinite(durationSeconds) && durationSeconds > 0
            ? durationSeconds
            : 0;
    const frameMax = Math.max(
        REF_FRAME_MIN,
        framesForClip(authoredDuration, safeFps, frameGrid),
    );
    if (
        !Number.isFinite(pointerXWithinRegion) ||
        !Number.isFinite(regionWidthPx) ||
        regionWidthPx <= 0
    ) {
        return REF_FRAME_MIN;
    }
    const fraction = clamp(pointerXWithinRegion / regionWidthPx, 0, 1);
    const effectiveDuration = frameMax / safeFps;
    const time = fraction * effectiveDuration;
    const rawFrame = fromEnd
        ? (effectiveDuration - time) * safeFps
        : time * safeFps;
    return clamp(Math.round(rawFrame), REF_FRAME_MIN, frameMax);
};

export const clampClipRefsToDuration = (
    clip: Clip,
    defaults: RootDefaults,
    effectiveFps?: number,
): void => {
    const frameMax = getKnownReferenceFrameMax(defaults, clip, effectiveFps);
    for (const ref of clip.frameRefs) {
        ref.frame =
            frameMax === null
                ? Math.max(REF_FRAME_MIN, Math.round(ref.frame))
                : clamp(ref.frame, REF_FRAME_MIN, frameMax);
    }
};

/**
 * Snaps here rather than only in normalization: an unsnapped duration survives
 * until the document round-trips through the carrier, so until then the number
 * in the detail strip and the clip drawn on the timeline disagree.
 */
export const applyClipDurationResize = (
    clip: Clip,
    newDuration: number,
    defaults: RootDefaults,
    effectiveFps?: number,
): boolean => {
    const snapped =
        effectiveFps === undefined
            ? newDuration
            : snapDurationToFps(newDuration, effectiveFps);
    if (clip.duration === snapped) {
        return false;
    }
    clip.duration = snapped;
    clampClipRefsToDuration(clip, defaults, effectiveFps);
    return true;
};
