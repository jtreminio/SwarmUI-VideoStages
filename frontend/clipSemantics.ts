import type { Clip } from "./types";

export const activeStageCount = (clip: Pick<Clip, "stages">): number =>
    clip.stages.filter((stage) => !stage.skipped).length;

export const isExecutableClip = (clip: Clip): boolean =>
    !clip.skipped && (clip.sourceVideo !== null || activeStageCount(clip) > 0);

export const executableClipIndexes = (clips: readonly Clip[]): number[] =>
    clips.flatMap((clip, index) => (isExecutableClip(clip) ? [index] : []));
