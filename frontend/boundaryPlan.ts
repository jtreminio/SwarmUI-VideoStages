import {
    type BoundaryWindowConstraints,
    boundaryWindowConstraints,
    normalizeBoundaryWindow,
} from "./architectures/boundaryConstraints";
import {
    type FrameGridSpec,
    framesForClip,
    NEUTRAL_FRAME_GRID,
} from "./renderUtils";
import type { BoundaryOut, Clip } from "./types";

export type BoundaryConstraintResolver = (
    leftClip: Clip,
    leftClipIdx: number,
    mode: BoundaryOut,
) => BoundaryWindowConstraints;

export type ClipFrameGridResolver = (
    clip: Clip,
    clipIndex: number,
) => FrameGridSpec;

export interface BoundaryPlan {
    /** Overlap frames removed at each interior boundary i (between clip i and i+1). */
    overlaps: number[];
    /** Planned continuity context requested for the next generation. */
    continuityWindows: number[];
    /** True when every requested non-cut boundary degraded to a hard cut. */
    fallback: boolean;
}

/**
 * Frontend preview of backend boundary budgeting. An overlap may shrink only
 * along its architecture's minimum-relative grid; when the minimum still
 * cannot fit, that overlap becomes a cut.
 */
export const boundaryPlanForClips = (
    clips: Clip[],
    fps: number,
    resolveConstraints: BoundaryConstraintResolver = (clip, _index, mode) => {
        const generic = boundaryWindowConstraints(null);
        const persisted = Math.trunc(Number(clip.boundaryOutOverlap));
        return {
            ...generic,
            defaultFrames:
                mode === "cut" || !Number.isFinite(persisted) || persisted <= 0
                    ? generic.defaultFrames
                    : persisted,
        };
    },
    resolveFrameGrid: ClipFrameGridResolver = () => NEUTRAL_FRAME_GRID,
): BoundaryPlan => {
    const count = clips.length;
    const boundaryCount = Math.max(0, count - 1);
    const zeroBoundaries = (): number[] => new Array(boundaryCount).fill(0);
    if (count < 2) {
        return {
            overlaps: zeroBoundaries(),
            continuityWindows: zeroBoundaries(),
            fallback: false,
        };
    }
    const modes: BoundaryOut[] = [];
    for (let i = 0; i < count - 1; i++) {
        const b = clips[i].boundaryOut ?? "cut";
        modes[i] = b;
    }
    const frames = clips.map((clip, index) =>
        framesForClip(clip.duration, fps, resolveFrameGrid(clip, index)),
    );
    const constraints = clips.map((clip, index) =>
        resolveConstraints(clip, index, clip.boundaryOut ?? "cut"),
    );
    const prefs = clips.map((clip, index) =>
        normalizeBoundaryWindow(clip.boundaryOutOverlap, constraints[index]),
    );
    const hasRequestedBoundary = modes.some((mode) => mode !== "cut");
    const active = (index: number): boolean =>
        modes[index] === "crossfade" ||
        (modes[index] === "continue" &&
            constraints[index].continueMode === "overlap");
    const trim = (index: number): number =>
        modes[index] === "continue"
            ? constraints[index].continueMode === "overlap"
                ? prefs[index] + constraints[index].continuityExtraFrames
                : 0
            : modes[index] === "crossfade"
              ? prefs[index]
              : 0;
    const hasBudgetedOverlap = modes.some((_mode, index) => active(index));
    const continuityWindows = (): number[] =>
        modes
            .slice(0, boundaryCount)
            .map((mode, index) =>
                mode === "continue"
                    ? constraints[index].continueMode === "reference"
                        ? prefs[index]
                        : trim(index)
                    : 0,
            );
    if (!hasBudgetedOverlap) {
        return {
            overlaps: zeroBoundaries(),
            continuityWindows: continuityWindows(),
            fallback: false,
        };
    }
    while (true) {
        let overBudgetClip = -1;
        for (let i = 0; i < count; i++) {
            const left = i > 0 ? trim(i - 1) : 0;
            const right = i < boundaryCount ? trim(i) : 0;
            const incomingHandle =
                i > 0 &&
                modes[i - 1] === "continue" &&
                constraints[i - 1].continueMode === "overlap"
                    ? prefs[i - 1]
                    : 0;
            if (left + right > frames[i] + incomingHandle - 1) {
                overBudgetClip = i;
                break;
            }
        }
        if (overBudgetClip < 0) break;
        const candidate =
            overBudgetClip < boundaryCount && active(overBudgetClip)
                ? overBudgetClip
                : overBudgetClip > 0 && active(overBudgetClip - 1)
                  ? overBudgetClip - 1
                  : -1;
        if (candidate < 0) {
            for (let i = 0; i < boundaryCount; i++) {
                if (active(i)) modes[i] = "cut";
            }
            return {
                overlaps: zeroBoundaries(),
                continuityWindows: continuityWindows(),
                fallback: !modes.some((mode) => mode !== "cut"),
            };
        }
        const reduced = prefs[candidate] - constraints[candidate].frameStep;
        if (reduced < constraints[candidate].minFrames) {
            modes[candidate] = "cut";
            prefs[candidate] = 0;
        } else {
            prefs[candidate] = reduced;
        }
    }
    const overlaps = modes
        .slice(0, boundaryCount)
        .map((_mode, index) => trim(index));
    return {
        overlaps,
        continuityWindows: continuityWindows(),
        fallback: hasRequestedBoundary && !modes.some((mode) => mode !== "cut"),
    };
};
