import type { TimelineTiming } from "./timelineTiming";
import { clipTrimSeconds } from "./timelineView/layout";
import type { Clip } from "./types";

export const SNAP_THRESHOLD_PX = 8;

const nearestTarget = (
    value: number,
    targets: readonly number[],
    threshold: number,
): number | null => {
    let nearest: number | null = null;
    let nearestDistance = Number.POSITIVE_INFINITY;
    for (const target of targets) {
        const distance = Math.abs(value - target);
        if (distance <= threshold && distance < nearestDistance) {
            nearest = target;
            nearestDistance = distance;
        }
    }
    return nearest;
};

export const snapPoint = (
    value: number,
    primaryTargets: readonly number[],
    fallbackTargets: readonly number[],
    threshold: number,
): number =>
    nearestTarget(value, primaryTargets, threshold) ??
    nearestTarget(value, fallbackTargets, threshold) ??
    value;

/** The moved start that puts whichever edge snaps closer onto its target. */
const snapStartAgainst = (
    start: number,
    length: number,
    targets: readonly number[],
    threshold: number,
): number | null => {
    const nearStart = nearestTarget(start, targets, threshold);
    const nearEnd = nearestTarget(start + length, targets, threshold);
    if (nearStart === null) {
        return nearEnd === null ? null : nearEnd - length;
    }
    if (nearEnd === null) {
        return nearStart;
    }
    return Math.abs(nearStart - start) <= Math.abs(nearEnd - (start + length))
        ? nearStart
        : nearEnd - length;
};

export const snapMovedStart = (
    start: number,
    length: number,
    primaryTargets: readonly number[],
    fallbackTargets: readonly number[],
    threshold: number,
): number =>
    snapStartAgainst(start, length, primaryTargets, threshold) ??
    snapStartAgainst(start, length, fallbackTargets, threshold) ??
    start;

export const timelineClipEdges = (
    clips: readonly Clip[],
    timing?: TimelineTiming,
): number[] => {
    if (timing) {
        const boundaryAfter = new Map(
            timing.boundaries.map((boundary) => [boundary.leftIdx, boundary]),
        );
        const boundaryBefore = new Map(
            timing.boundaries.map((boundary) => [boundary.rightIdx, boundary]),
        );
        const edges: number[] = [0];
        let cursor = 0;
        for (const clipIdx of timing.executableClipIndexes) {
            const duration = (timing.clipFrames[clipIdx] ?? 0) / timing.fps;
            const incoming = boundaryBefore.get(clipIdx);
            const outgoing = boundaryAfter.get(clipIdx);
            // Known divergence, not a design: these edges are always measured
            // on the compacted output ruler, while a dormant clip puts the
            // cards on the authored one. Both rulers would have to move.
            const trim = clipTrimSeconds(incoming, outgoing, true);
            const editEnd =
                cursor + Math.max(0, duration - trim.before - trim.after);
            edges.push(cursor, editEnd);
            if (outgoing && outgoing.overlapSeconds > 0) {
                edges.push(
                    outgoing.effectiveMode === "continue"
                        ? editEnd - outgoing.overlapSeconds
                        : editEnd - outgoing.overlapSeconds / 2,
                    outgoing.effectiveMode === "continue"
                        ? editEnd
                        : editEnd + outgoing.overlapSeconds / 2,
                );
            }
            cursor = editEnd;
        }
        edges.push(timing.outputSeconds);
        return edges
            .sort((left, right) => left - right)
            .filter(
                (edge, index, sorted) =>
                    index === 0 || Math.abs(edge - sorted[index - 1]) > 1e-9,
            );
    }
    const edges = [0];
    let cursor = 0;
    for (const clip of clips) {
        cursor += Math.max(0, clip.duration || 0);
        edges.push(cursor);
    }
    return edges;
};
