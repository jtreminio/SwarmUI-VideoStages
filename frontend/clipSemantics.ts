import type { Clip } from "./types";

/** Stages execute only until the first authored skip marker. */
export const activeStageCount = (clip: Pick<Clip, "stages">): number => {
    const firstSkipped = clip.stages.findIndex(
        (stage) => stage.skipped === true,
    );
    return firstSkipped < 0 ? clip.stages.length : firstSkipped;
};

export const isExecutableClip = (clip: Clip): boolean =>
    !clip.skipped && (clip.sourceVideo !== null || activeStageCount(clip) > 0);

export const executableClipIndexes = (clips: readonly Clip[]): number[] => {
    const firstSkipped = clips.findIndex((clip) => clip.skipped === true);
    const prefix = firstSkipped < 0 ? clips : clips.slice(0, firstSkipped);
    return prefix.flatMap((clip, index) =>
        isExecutableClip(clip) ? [index] : [],
    );
};

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
