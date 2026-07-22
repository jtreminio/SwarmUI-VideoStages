import { framesForClip } from "./renderUtils";
import type { Clip } from "./types";

// Mirrors MultiClipParallelMerger.DefaultCrossfadeOverlapFrames.
export const DEFAULT_CROSSFADE_OVERLAP_FRAMES = 8;
// Mirror Constants.ContinueOverlapDefaultFrames / ContinueOverlapMaxFrames.
export const DEFAULT_CONTINUE_OVERLAP_FRAMES = 8;
export const CONTINUE_OVERLAP_MAX_FRAMES = 48;
export const CONTINUE_OVERLAP_CHOICES = [8, 16, 24, 32, 40, 48] as const;

export interface BoundaryPlan {
    /** Overlap frames removed at each interior boundary i (between clip i and i+1). */
    overlaps: number[];
    /** True when the whole plan degraded to a hard cut (a clip is too short for the overlaps). */
    fallback: boolean;
}

/**
 * Frontend mirror of the backend `MultiClipParallelMerger.ResolveContinueWindows`. A "continue"
 * boundary's window is the requested overlap + 1 (an 8n+1 frame count, matching the LTX causal VAE
 * grid), stepped down (… 17, 9, 1) until both adjacent clips keep >=1 core frame — reserving one
 * frame on each neighbour that funds another overlapping boundary of its own.
 */
const resolveContinueWindows = (
    frames: number[],
    cont: boolean[],
    crossfade: boolean[],
    requested: number[],
): number[] => {
    const windows: number[] = new Array(cont.length).fill(0);
    for (let i = 0; i < cont.length; i++) {
        if (!cont[i]) {
            continue;
        }
        let k = (requested[i] ?? DEFAULT_CONTINUE_OVERLAP_FRAMES) + 1;
        const leftReserve =
            i > 0 && cont[i - 1]
                ? windows[i - 1]
                : i > 0 && crossfade[i - 1]
                  ? 1
                  : 0;
        const rightReserve =
            i < cont.length - 1 && (cont[i + 1] || crossfade[i + 1]) ? 1 : 0;
        const kMax = Math.min(
            frames[i] - 1 - leftReserve,
            frames[i + 1] - 1 - rightReserve,
        );
        while (k > 1 && k > kMax) {
            k -= 8;
        }
        windows[i] = Math.max(1, k);
    }
    return windows;
};

/**
 * Frontend mirror of the backend `ResolveCrossfadePlan` overlap math (a crossfade boundary's
 * requested dissolve clamps to both clips' budgets — a clip funding two crossfades splits its budget
 * evenly; a "continue" boundary takes its resolved overlap+1 window). On this branch dims/fps are
 * timeline-uniform, so the only fallback the frontend can detect is a clip too short to fund its
 * overlaps; the LTX-2 model-family gate is enforced by the backend (authoritative) and is not
 * checked here.
 */
export const crossfadePlanForClips = (
    clips: Clip[],
    fps: number,
): BoundaryPlan => {
    const count = clips.length;
    const boundaryCount = Math.max(0, count - 1);
    const noOverlap = (): number[] => new Array(boundaryCount).fill(0);
    if (count < 2) {
        return { overlaps: noOverlap(), fallback: false };
    }
    const crossfade: boolean[] = [];
    const cont: boolean[] = [];
    let requested = 0;
    for (let i = 0; i < count - 1; i++) {
        const b = clips[i].boundaryOut ?? "cut";
        crossfade[i] = b === "crossfade";
        cont[i] = b === "continue";
        if (crossfade[i] || cont[i]) {
            requested++;
        }
    }
    if (requested === 0) {
        return { overlaps: noOverlap(), fallback: false };
    }
    const frames = clips.map((c) => framesForClip(c.duration, fps));
    const prefs = clips.map(
        (c) => c.boundaryOutOverlap ?? DEFAULT_CONTINUE_OVERLAP_FRAMES,
    );
    const windows = resolveContinueWindows(frames, cont, crossfade, prefs);
    const crossfadeMaxPerSide: number[] = new Array(count).fill(0);
    for (let i = 0; i < count; i++) {
        const fixedTrim =
            (i > 0 && cont[i - 1] ? windows[i - 1] : 0) +
            (i < count - 1 && cont[i] ? windows[i] : 0);
        const crossSides =
            (i > 0 && crossfade[i - 1] ? 1 : 0) +
            (i < count - 1 && crossfade[i] ? 1 : 0);
        if (fixedTrim === 0 && crossSides === 0) {
            continue;
        }
        const budget = frames[i] - 1 - fixedTrim;
        if (
            budget < 0 ||
            (crossSides > 0 && Math.floor(budget / crossSides) < 1)
        ) {
            return { overlaps: noOverlap(), fallback: true };
        }
        if (crossSides > 0) {
            crossfadeMaxPerSide[i] = Math.floor(budget / crossSides);
        }
    }
    const overlaps: number[] = [];
    for (let i = 0; i < count - 1; i++) {
        overlaps[i] = crossfade[i]
            ? Math.min(
                  Math.max(1, prefs[i] ?? DEFAULT_CROSSFADE_OVERLAP_FRAMES),
                  crossfadeMaxPerSide[i],
                  crossfadeMaxPerSide[i + 1],
              )
            : cont[i]
              ? windows[i]
              : 0;
    }
    return { overlaps, fallback: false };
};
