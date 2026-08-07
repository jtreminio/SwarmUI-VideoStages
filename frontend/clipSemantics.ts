import type { Clip } from "./types";

interface Skippable {
    skipped?: boolean;
}

/**
 * Skip is a suffix: everything from the first skipped item onward is skipped.
 * This file is the only place that rule is written down — read it with
 * `activePrefix`, write it with `applySkipSuffix` or `sealSkipSuffix`.
 */
export const activePrefix = <T extends Skippable>(items: readonly T[]): T[] => {
    const firstSkipped = items.findIndex((item) => item.skipped === true);
    return firstSkipped < 0 ? [...items] : items.slice(0, firstSkipped);
};

/**
 * Sets `skipped` on `items[fromIndex]` and everything after it. Un-skipping
 * starts at the first skipped item instead, so the suffix never has a hole.
 */
export const applySkipSuffix = <T extends Skippable>(
    items: T[],
    fromIndex: number,
    skipped: boolean,
): void => {
    const firstSkipped = items.findIndex((item) => item.skipped === true);
    const start = skipped ? fromIndex : Math.max(0, firstSkipped);
    for (let index = start; index < items.length; index++) {
        items[index].skipped = skipped;
    }
};

/** Repairs a list whose skip marker exists but whose suffix is incomplete. */
export const sealSkipSuffix = <T extends Skippable>(items: T[]): void => {
    const firstSkipped = items.findIndex((item) => item.skipped === true);
    if (firstSkipped >= 0) {
        applySkipSuffix(items, firstSkipped, true);
    }
};

export const activeStageCount = (clip: Pick<Clip, "stages">): number =>
    activePrefix(clip.stages).length;

export const isExecutableClip = (clip: Clip): boolean =>
    !clip.skipped && (clip.initVideo !== null || activeStageCount(clip) > 0);

export const executableClipIndexes = (clips: readonly Clip[]): number[] =>
    activePrefix(clips).flatMap((clip, index) =>
        isExecutableClip(clip) ? [index] : [],
    );

/**
 * One join the backend actually compiles: the pair of adjacent EXECUTABLE clips,
 * after the first skip marker truncated and earlier non-executable clips
 * compacted away. Every consumer — seam chips, click selection, labels,
 * overlap previews, diagnostics — reads its neighbours from here so the UI can
 * never show a seam execution does not have.
 */
export interface ExecutableBoundary {
    /** Position in the compacted executable list; indexes the compiled boundary plan. */
    position: number;
    leftIdx: number;
    rightIdx: number;
    leftId?: string;
    rightId?: string;
}

export const executableBoundaries = (
    clips: readonly Clip[],
): ExecutableBoundary[] => {
    const indexes = executableClipIndexes(clips);
    const boundaries: ExecutableBoundary[] = [];
    for (let position = 0; position < indexes.length - 1; position++) {
        const leftIdx = indexes[position];
        const rightIdx = indexes[position + 1];
        boundaries.push({
            position,
            leftIdx,
            rightIdx,
            leftId: clips[leftIdx].id,
            rightId: clips[rightIdx].id,
        });
    }
    return boundaries;
};

/** The executable join leaving `leftClipIdx`, or null when that clip has none. */
export const executableBoundaryForLeftClip = (
    clips: readonly Clip[],
    leftClipIdx: number,
): ExecutableBoundary | null =>
    executableBoundaries(clips).find(
        (boundary) => boundary.leftIdx === leftClipIdx,
    ) ?? null;
