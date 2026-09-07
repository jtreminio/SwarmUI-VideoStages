import { AUDIO_SOURCE_PREVIOUS_CLIP, audioSourceKind } from "./audioSource";
import { executableClipIndexes } from "./clipSemantics";
import { MEDIA_SOURCE_PREVIOUS_CLIP } from "./generatedMediaSource";
import type { Clip } from "./types";

export type TimelineDependencyKind = "audio-base" | "video-source";

export interface TimelineDependency {
    sourceClipIdx: number;
    targetClipIdx: number;
    kind: TimelineDependencyKind;
}

export interface TimelinePublication {
    executableClipIndexes: number[];
    outputClipIndexes: number[];
    consumedClipIndexes: number[];
    dependencies: TimelineDependency[];
}

/** The authored generation steps projected onto the artifacts the runtime publishes. */
export const resolveTimelinePublication = (
    clips: readonly Clip[],
): TimelinePublication => {
    const executable = executableClipIndexes(clips);
    const output: number[] = [];
    const dependencies: TimelineDependency[] = [];

    for (let position = 0; position < executable.length; position++) {
        const clipIdx = executable[position];
        const clip = clips[clipIdx];
        const previousIdx = executable[position - 1];
        const previous = clips[previousIdx];
        const hasPrevious = previous !== undefined;
        const usesPreviousVideo =
            hasPrevious &&
            previous.audioOnly !== true &&
            clip.initVideo?.source === MEDIA_SOURCE_PREVIOUS_CLIP;

        if (hasPrevious) {
            if (usesPreviousVideo) {
                dependencies.push({
                    sourceClipIdx: previousIdx,
                    targetClipIdx: clipIdx,
                    kind: "video-source",
                });
            }
            const selectedAudio = audioSourceKind(clip.audioSource);
            if (
                previous.audioOnly === true ||
                selectedAudio === AUDIO_SOURCE_PREVIOUS_CLIP
            ) {
                dependencies.push({
                    sourceClipIdx: previousIdx,
                    targetClipIdx: clipIdx,
                    kind: "audio-base",
                });
            }
        }

        if (clip.audioOnly === true) {
            continue;
        }
        if (usesPreviousVideo && output.at(-1) === previousIdx) {
            output.pop();
        }
        output.push(clipIdx);
    }

    if (output.length === 0 && executable.length > 0) {
        output.push(executable.at(-1) as number);
    }
    const published = new Set(output);
    return {
        executableClipIndexes: executable,
        outputClipIndexes: output,
        consumedClipIndexes: executable.filter(
            (index) => !published.has(index),
        ),
        dependencies,
    };
};
