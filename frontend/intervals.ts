/**
 * Shared 1-D occupancy math for lane items that must not overlap (today only
 * relay prompt windows — audio segments moved to per-segment lanes and may
 * overlap freely). A track builds the sorted spans of every OTHER item, then
 * asks for the free interval around a point — the walls the item may
 * move/resize within.
 */

export interface Span {
    start: number;
    end: number;
}

/**
 * The maximal free interval [lo, hi] containing `point`, given sorted,
 * non-overlapping occupied `spans` inside [0, total]. A point INSIDE an
 * occupied span collapses to [p, p].
 */
export const freeIntervalAt = (
    spans: Span[],
    total: number,
    point: number,
): [number, number] => {
    const p = Math.min(Math.max(point, 0), total);
    let lo = 0;
    let hi = total;
    for (const span of spans) {
        if (span.end <= p) {
            if (span.end > lo) {
                lo = span.end;
            }
        } else if (span.start >= p) {
            hi = span.start;
            break;
        } else {
            return [p, p];
        }
    }
    return [lo, hi];
};
