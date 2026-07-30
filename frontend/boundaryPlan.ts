import {
    type BoundaryOverlapConstraints,
    boundaryOverlapConstraints,
    normalizeBoundaryOverlap,
} from "./architectures/boundaryConstraints";
import { framesForClip } from "./renderUtils";
import type { BoundaryOut, Clip } from "./types";

export type BoundaryConstraintResolver = (
    leftClip: Clip,
    leftClipIdx: number,
    mode: BoundaryOut,
) => BoundaryOverlapConstraints;

export type ClipFrameGridResolver = (clip: Clip, clipIndex: number) => number;

export interface BoundaryPlan {
    /** Overlap frames removed at each interior boundary i (between clip i and i+1). */
    overlaps: number[];
    /** True when the whole plan degraded to a hard cut (a clip is too short for the overlaps). */
    fallback: boolean;
}

/**
 * Frontend preview of backend overlap budgeting. A boundary may shrink only
 * along its architecture's minimum-relative grid; when the minimum still
 * cannot fit, that boundary becomes a cut.
 */
export const crossfadePlanForClips = (
    clips: Clip[],
    fps: number,
    resolveConstraints: BoundaryConstraintResolver = (clip, _index, mode) => {
        const generic = boundaryOverlapConstraints(null);
        const persisted = Math.trunc(Number(clip.boundaryOutOverlap));
        return {
            ...generic,
            defaultFrames:
                mode === "cut" || !Number.isFinite(persisted) || persisted <= 0
                    ? generic.defaultFrames
                    : persisted,
        };
    },
    resolveFrameGrid: ClipFrameGridResolver = () => 1,
): BoundaryPlan => {
    const count = clips.length;
    const boundaryCount = Math.max(0, count - 1);
    const noOverlap = (): number[] => new Array(boundaryCount).fill(0);
    if (count < 2) {
        return { overlaps: noOverlap(), fallback: false };
    }
    let requested = 0;
    const modes: BoundaryOut[] = [];
    for (let i = 0; i < count - 1; i++) {
        const b = clips[i].boundaryOut ?? "cut";
        modes[i] = b;
        if (b === "crossfade" || b === "continue") {
            requested++;
        }
    }
    if (requested === 0) {
        return { overlaps: noOverlap(), fallback: false };
    }
    const frames = clips.map((clip, index) =>
        framesForClip(clip.duration, fps, resolveFrameGrid(clip, index)),
    );
    const constraints = clips.map((clip, index) =>
        resolveConstraints(clip, index, clip.boundaryOut ?? "cut"),
    );
    const prefs = clips.map((clip, index) =>
        normalizeBoundaryOverlap(clip.boundaryOutOverlap, constraints[index]),
    );
    const active = (index: number): boolean =>
        modes[index] === "continue" || modes[index] === "crossfade";
    const trim = (index: number): number =>
        modes[index] === "continue"
            ? prefs[index] + constraints[index].continuityExtraFrames
            : modes[index] === "crossfade"
              ? prefs[index]
              : 0;
    while (true) {
        let overBudgetClip = -1;
        for (let i = 0; i < count; i++) {
            const left = i > 0 ? trim(i - 1) : 0;
            const right = i < boundaryCount ? trim(i) : 0;
            if (left + right > frames[i] - 1) {
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
            return { overlaps: noOverlap(), fallback: true };
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
        fallback: requested > 0 && !modes.some((_mode, index) => active(index)),
    };
};
