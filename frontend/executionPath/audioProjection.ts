import {
    audioReuseMinimumActiveStages,
    CONDITIONAL_RULE_CODES,
    conditionalRule,
} from "../architectures/conditionalRules";
import type { ArchitectureModelCatalog } from "../architectures/types";
import type { AudioTrack, AudioTrackSpan, VideoStagesConfig } from "../types";
import { audioSourceLabel, plural } from "./formatters";
import type { ProjectedClipEntry } from "./projectionTypes";
import type {
    VideoAuthoredAudioSpanSummary,
    VideoAuthoredAudioTrackSummary,
    VideoClipAudioSummary,
} from "./types";

const describeAuthoredAudioSpan = (
    track: AudioTrack,
    span: AudioTrackSpan,
    spanIndex: number,
    clipIndexById: ReadonlyMap<string, number>,
    clipCount: number,
): VideoAuthoredAudioSpanSummary => {
    const firstIndex = span.firstClipId
        ? clipIndexById.get(span.firstClipId)
        : undefined;
    const lastIndex = span.lastClipId
        ? clipIndexById.get(span.lastClipId)
        : undefined;
    const hasClipRange = span.firstClipId !== null || span.lastClipId !== null;
    const missingEndpoint =
        (span.firstClipId !== null && firstIndex === undefined) ||
        (span.lastClipId !== null && lastIndex === undefined);
    const rangeStart = span.firstClipId === null ? 0 : firstIndex;
    const rangeEnd = span.lastClipId === null ? clipCount - 1 : lastIndex;
    const reversed =
        rangeStart !== undefined &&
        rangeEnd !== undefined &&
        rangeStart > rangeEnd;
    const clipNumbers =
        hasClipRange &&
        !missingEndpoint &&
        !reversed &&
        rangeStart !== undefined &&
        rangeEnd !== undefined
            ? Array.from(
                  { length: rangeEnd - rangeStart + 1 },
                  (_, offset) => rangeStart + offset + 1,
              )
            : [];
    const timelineFields = [
        span.timelineStartSeconds,
        span.timelineLengthSeconds,
    ];
    const hasTimelineWindow = timelineFields.some((value) => value !== null);
    const incompleteTimelineWindow =
        hasTimelineWindow && timelineFields.some((value) => value === null);
    const clipRelativeFields = [
        span.clipStartOffsetSeconds,
        span.clipLengthSeconds,
    ];
    const hasClipRelativeWindow = clipRelativeFields.some(
        (value) => value !== null,
    );
    const incompleteClipRelativeWindow =
        hasClipRelativeWindow &&
        clipRelativeFields.some((value) => value === null);
    const clipRelativeRangeInvalid =
        hasClipRelativeWindow &&
        (span.firstClipId === null ||
            span.lastClipId === null ||
            span.firstClipId !== span.lastClipId ||
            clipNumbers.length !== 1 ||
            missingEndpoint ||
            reversed);
    const pending =
        missingEndpoint ||
        reversed ||
        incompleteTimelineWindow ||
        incompleteClipRelativeWindow ||
        clipRelativeRangeInvalid ||
        (!hasClipRange && !hasTimelineWindow && !hasClipRelativeWindow);
    const coverage =
        clipNumbers.length === 0
            ? hasTimelineWindow
                ? "timeline window"
                : "unresolved clip coverage"
            : clipNumbers.length === 1
              ? `clip ${clipNumbers[0]}`
              : `clips ${clipNumbers[0]}–${clipNumbers.at(-1)}`;
    const timeline =
        span.timelineStartSeconds !== null &&
        span.timelineLengthSeconds !== null
            ? ` · timeline ${span.timelineStartSeconds}–${span.timelineStartSeconds + span.timelineLengthSeconds}s`
            : "";
    const source =
        span.sourceStartSeconds > 0
            ? ` · source +${span.sourceStartSeconds}s`
            : "";
    const clipRelative =
        span.clipStartOffsetSeconds !== null && span.clipLengthSeconds !== null
            ? ` · within clip ${span.clipStartOffsetSeconds}–${span.clipStartOffsetSeconds + span.clipLengthSeconds}s`
            : "";
    return {
        trackId: track.id ?? "",
        spanId: span.id ?? "",
        firstClipNumber: firstIndex === undefined ? null : firstIndex + 1,
        lastClipNumber: lastIndex === undefined ? null : lastIndex + 1,
        clipNumbers,
        pending,
        label: `Span ${spanIndex + 1}: ${coverage}${timeline}${clipRelative}${source}${pending ? " · pending" : ""}`,
    };
};

export const projectAuthoredAudioTracks = (
    config: VideoStagesConfig,
): VideoAuthoredAudioTrackSummary[] => {
    const clipIndexById = new Map<string, number>();
    config.clips.forEach((clip, index) => {
        if (clip.id) {
            clipIndexById.set(clip.id, index);
        }
    });
    return (config.audioTracks ?? []).map((track, trackIndex) => {
        const spans = track.spans.map((span, spanIndex) =>
            describeAuthoredAudioSpan(
                track,
                span,
                spanIndex,
                clipIndexById,
                config.clips.length,
            ),
        );
        const clipNumbers = [
            ...new Set(spans.flatMap((span) => span.clipNumbers)),
        ].sort((a, b) => a - b);
        const pendingSpanCount = spans.filter((span) => span.pending).length;
        const sourceUploadFileName =
            track.source.uploadedAudio?.fileName?.trim() || null;
        const source =
            track.source.reference.trim() ||
            sourceUploadFileName ||
            `${track.source.kind} source pending`;
        const coverage =
            clipNumbers.length === 0
                ? "unresolved coverage"
                : clipNumbers.length === 1
                  ? `clip ${clipNumbers[0]}`
                  : `clips ${clipNumbers[0]}–${clipNumbers.at(-1)}`;
        return {
            trackId: track.id ?? "",
            sourceKind: track.source.kind,
            sourceReference: track.source.reference,
            sourceUploadFileName,
            spanCount: spans.length,
            clipNumbers,
            pendingSpanCount,
            spans,
            label: `Track ${trackIndex + 1}: ${source} · ${plural(spans.length, "span")} · ${coverage}${pendingSpanCount > 0 ? ` · ${plural(pendingSpanCount, "pending span")}` : ""}`,
        };
    });
};

export const projectClipAudio = (
    activeEntries: readonly ProjectedClipEntry[],
    catalog?: ArchitectureModelCatalog,
): VideoClipAudioSummary[] =>
    activeEntries.map(({ clip, index, stages }) => {
        const descriptor = catalog?.architectures.find(
            (entry) => entry.id === clip.architecture,
        );
        const reuseRule = descriptor
            ? conditionalRule(
                  descriptor.rules,
                  CONDITIONAL_RULE_CODES.audioReuseRequiresStages,
              )
            : null;
        return {
            clipNumber: index + 1,
            source: clip.audioSource,
            label: `Clip ${index + 1}: ${audioSourceLabel(clip.audioSource)} audio`,
            lengthFromAudio: clip.clipLengthFromAudio === true,
            lengthFromControlNet: clip.clipLengthFromControlNet === true,
            reusesStageAudio:
                clip.reuseAudio === true &&
                stages.length >= audioReuseMinimumActiveStages(reuseRule),
            savesTrack: clip.saveAudioTrack === true,
            segmentCount: clip.audioSegments.length,
        };
    });
