import {
    projectAuthoredAudioTracks,
    projectClipAudio,
} from "./executionPath/audioProjection";
import { projectBoundaries } from "./executionPath/boundaryProjection";
import {
    audioSourceLabel,
    describeClip,
    describeShape,
    HOST_ENTRY_LABEL,
    plural,
    referenceSourceLabel,
} from "./executionPath/formatters";
import type {
    ProjectedClipEntry,
    ProjectedStage,
} from "./executionPath/projectionTypes";
import type {
    VideoExecutionContext,
    VideoExecutionPathSummary,
    VideoHostEntryHint,
} from "./executionPath/types";
import {
    type Clip,
    REF_SOURCE_UPLOAD,
    type RefImage,
    type VideoStagesConfig,
} from "./types";

export type {
    VideoAuthoredAudioSpanSummary,
    VideoAuthoredAudioTrackSummary,
    VideoBoundarySummary,
    VideoClipAudioSummary,
    VideoClipExecutionKind,
    VideoClipPathSummary,
    VideoExecutionContext,
    VideoExecutionPathSummary,
    VideoHostEntryHint,
    VideoReferenceSummary,
    VideoTimelineShape,
} from "./executionPath/types";

const projectedStages = (
    clip: Clip,
    clipIndex: number,
    context: VideoExecutionContext,
): ProjectedStage[] => {
    const refineSkip =
        context.entryPoint === "global-refine-video" && clipIndex === 0
            ? Math.max(0, context.refineSkipStages ?? 1)
            : 0;
    return clip.stages.flatMap((stage, rawIndex) =>
        stage.skipped !== true && rawIndex >= refineSkip
            ? [{ stage, rawIndex }]
            : [],
    );
};

const activeStages = (clip: Clip): Clip["stages"] =>
    clip.stages.filter((stage) => stage.skipped !== true);

const isExecutable = (entry: ProjectedClipEntry): boolean =>
    entry.clip.skipped !== true &&
    (entry.clip.sourceVideo !== null || entry.stages.length > 0);

const isInitialGuidance = (reference: RefImage): boolean =>
    reference.fromEnd !== true &&
    Math.max(1, Math.round(reference.frame)) === 1;

const inferHostEntry = (
    clips: readonly Clip[],
    context: VideoExecutionContext,
): VideoHostEntryHint => {
    if (context.entryPoint) {
        return context.entryPoint;
    }
    const firstClip = clips[0];
    if (firstClip?.sourceVideo) {
        return activeStages(firstClip).length === 0
            ? "source-video-only"
            : "source-video";
    }
    const initialReferences = firstClip?.refs.filter(isInitialGuidance) ?? [];
    if (
        initialReferences.some(
            (reference) => reference.source === REF_SOURCE_UPLOAD,
        )
    ) {
        return "init-image-guidance";
    }
    if (context.generatedEntry) {
        return context.generatedEntry;
    }
    return initialReferences.length > 0
        ? "host-image-guidance"
        : "text-to-video";
};

/**
 * Projects persisted editor state into a small architecture-aware explanation
 * of what the user will make. It describes paths and options, not backend
 * graph nodes or execution internals.
 */
export const projectVideoExecutionPath = (
    config: VideoStagesConfig,
    context: VideoExecutionContext = {},
): VideoExecutionPathSummary => {
    const entries: ProjectedClipEntry[] = config.clips.map((clip, index) => ({
        clip,
        index,
        stages: projectedStages(clip, index, context),
    }));
    const activeEntries = entries.filter(isExecutable);
    const clips = entries.map(({ clip, index, stages }) =>
        describeClip(clip, index + 1, stages, context),
    );
    const shape = describeShape(
        activeEntries.map(({ stages }) => stages.length),
    );
    const hostEntry = inferHostEntry(config.clips, context);
    const isTextToVideoRoot =
        context.generatedEntry !== undefined
            ? context.generatedEntry === "text-to-video"
            : context.entryPoint === "text-to-video" ||
              hostEntry === "text-to-video";
    const boundaries = projectBoundaries(activeEntries, context);
    const references = activeEntries.flatMap(({ clip, index }) =>
        clip.refs
            .filter(
                (reference) =>
                    !isTextToVideoRoot ||
                    reference.source === REF_SOURCE_UPLOAD,
            )
            .map((reference) => {
                const frame = Math.max(1, Math.round(reference.frame));
                const position = reference.fromEnd
                    ? `${plural(frame, "frame")} from end`
                    : `frame ${frame}`;
                return {
                    clipNumber: index + 1,
                    frame,
                    fromEnd: reference.fromEnd,
                    source: reference.source,
                    label: `Clip ${index + 1}: ${referenceSourceLabel(reference)} at ${position}`,
                };
            }),
    );
    const audioClips = projectClipAudio(activeEntries, context.catalog);
    const sourceVideoClipNumbers = activeEntries
        .filter(({ clip }) => clip.sourceVideo !== null)
        .map(({ index }) => index + 1);
    const sourceVideoOnlyClipNumbers = activeEntries
        .filter(
            ({ clip, stages }) =>
                clip.sourceVideo !== null && stages.length === 0,
        )
        .map(({ index }) => index + 1);
    const upscaledStageCount = activeEntries.reduce(
        (total, { clip, stages }) =>
            total +
            stages.filter(
                ({ stage, rawIndex }) =>
                    stage.upscale > 1 &&
                    (clip.sourceVideo !== null || rawIndex > 0),
            ).length,
        0,
    );
    const loraCount = activeEntries.reduce(
        (total, { stages }) =>
            total +
            stages.reduce(
                (stageTotal, { stage }) =>
                    stageTotal +
                    stage.loras.filter((lora) => lora.name.trim().length > 0)
                        .length,
                0,
            ),
        0,
    );
    const icLoraCount = activeEntries.reduce(
        (total, { clip, stages }) =>
            total +
            clip.icLoras.filter(
                (entry) =>
                    entry.lora.trim().length > 0 &&
                    (entry.stage < 0
                        ? stages.length > 0
                        : stages.some(
                              ({ rawIndex }) => rawIndex === entry.stage,
                          )),
            ).length,
        0,
    );
    const hasGlobalPrompt = `${context.globalPrompt ?? ""}`.trim().length > 0;
    const majorPromptOverrideClipNumbers = activeEntries
        .filter(({ clip }) => clip.prompt.trim().length > 0)
        .map(({ index }) => index + 1);
    const majorPromptInheritedClipNumbers = hasGlobalPrompt
        ? activeEntries
              .filter(({ clip }) => clip.prompt.trim().length === 0)
              .map(({ index }) => index + 1)
        : [];
    const majorPromptClipNumbers = [
        ...majorPromptOverrideClipNumbers,
        ...majorPromptInheritedClipNumbers,
    ].sort((a, b) => a - b);
    const relayPromptCount = activeEntries.reduce(
        (total, { clip }) =>
            total +
            clip.promptWindows.filter(
                (window) =>
                    window.duration > 0 && window.prompt.trim().length > 0,
            ).length,
        0,
    );
    const retakeClipNumbers = activeEntries
        .filter(
            ({ clip, stages }) =>
                clip.retake !== null &&
                stages.length > 0 &&
                (clip.sourceVideo !== null ||
                    context.entryPoint === "global-refine-video"),
        )
        .map(({ index }) => index + 1);
    const lengthFromAudioClipNumbers = audioClips
        .filter((clip) => clip.lengthFromAudio)
        .map((clip) => clip.clipNumber);
    const lengthFromControlNetClipNumbers = audioClips
        .filter((clip) => clip.lengthFromControlNet)
        .map((clip) => clip.clipNumber);
    const segmentCount = audioClips.reduce(
        (total, clip) => total + clip.segmentCount,
        0,
    );
    const authoredTracks = projectAuthoredAudioTracks(config);
    const authoredSpanCount = authoredTracks.reduce(
        (total, track) => total + track.spanCount,
        0,
    );
    const pendingAuthoredSpanCount = authoredTracks.reduce(
        (total, track) => total + track.pendingSpanCount,
        0,
    );
    const architectureLabels = [
        ...new Set(
            clips
                .filter((clip) => clip.kind !== "skipped")
                .map((clip) => clip.architectureLabel),
        ),
    ];
    const architectureSummary =
        architectureLabels.length === 0
            ? "No active architecture"
            : architectureLabels.length === 1
              ? architectureLabels[0]
              : `Mixed architectures: ${architectureLabels.join(", ")}`;
    const labels = [
        "VideoStages",
        architectureSummary,
        HOST_ENTRY_LABEL[hostEntry],
        shape.label,
    ];
    if (sourceVideoClipNumbers.length > 0) {
        labels.push(plural(sourceVideoClipNumbers.length, "source-video clip"));
    }
    if (sourceVideoOnlyClipNumbers.length > 0) {
        labels.push(
            plural(sourceVideoOnlyClipNumbers.length, "source-video-only clip"),
        );
    }
    if (boundaries.length > 0) {
        labels.push(
            `${plural(boundaries.length, "clip boundary", "clip boundaries")}: ${boundaries.map((boundary) => (boundary.fallback === "none" ? boundary.effective : `${boundary.requested}→${boundary.effective}`)).join(", ")}`,
        );
    }
    if (upscaledStageCount > 0) {
        labels.push(`Upscaling in ${plural(upscaledStageCount, "stage")}`);
    }
    if (icLoraCount > 0) labels.push(plural(icLoraCount, "IC-LoRA"));
    if (loraCount > 0) labels.push(plural(loraCount, "LoRA"));
    if (
        majorPromptOverrideClipNumbers.length > 0 ||
        majorPromptInheritedClipNumbers.length > 0
    ) {
        const promptSources = [
            ...(majorPromptOverrideClipNumbers.length > 0
                ? [
                      plural(
                          majorPromptOverrideClipNumbers.length,
                          "clip override",
                      ),
                  ]
                : []),
            ...(majorPromptInheritedClipNumbers.length > 0
                ? [
                      `${majorPromptInheritedClipNumbers.length} inherit${majorPromptInheritedClipNumbers.length === 1 ? "s" : ""} global`,
                  ]
                : []),
        ];
        labels.push(`Major prompts: ${promptSources.join(", ")}`);
    }
    if (relayPromptCount > 0) {
        labels.push(plural(relayPromptCount, "relay prompt"));
    }
    if (retakeClipNumbers.length > 0) {
        labels.push(plural(retakeClipNumbers.length, "retake"));
    }
    if (references.length > 0) {
        labels.push(plural(references.length, "frame reference"));
    }
    if (lengthFromAudioClipNumbers.length > 0) {
        labels.push(
            `Audio sets ${plural(lengthFromAudioClipNumbers.length, "clip length")}`,
        );
    }
    if (lengthFromControlNetClipNumbers.length > 0) {
        labels.push(
            `ControlNet sets ${plural(lengthFromControlNetClipNumbers.length, "clip length")}`,
        );
    }
    if (segmentCount > 0) labels.push(plural(segmentCount, "audio segment"));
    if (audioClips.length > 0) {
        labels.push(
            `Audio sources: ${[
                ...new Set(
                    audioClips.map((clip) => audioSourceLabel(clip.source)),
                ),
            ].join(", ")}`,
        );
    }
    const reuseCount = audioClips.filter(
        (clip) => clip.reusesStageAudio,
    ).length;
    if (reuseCount > 0) {
        labels.push(
            `${plural(reuseCount, "clip")} reuses captured stage audio`,
        );
    }
    const savedTrackCount = audioClips.filter((clip) => clip.savesTrack).length;
    if (savedTrackCount > 0) {
        labels.push(plural(savedTrackCount, "saved audio output"));
    }
    if (authoredTracks.length > 0) {
        labels.push(
            `${plural(authoredTracks.length, "planned audio track")} · ${plural(authoredSpanCount, "span")}${pendingAuthoredSpanCount > 0 ? ` · ${plural(pendingAuthoredSpanCount, "pending span")}` : ""}`,
        );
    }

    return {
        engine: "VideoStages",
        hostEntry: { kind: hostEntry, label: HOST_ENTRY_LABEL[hostEntry] },
        shape,
        counts: {
            clips: config.clips.length,
            executableClips: activeEntries.length,
            stages: config.clips.reduce(
                (total, clip) => total + clip.stages.length,
                0,
            ),
            activeStages: activeEntries.reduce(
                (total, entry) => total + entry.stages.length,
                0,
            ),
            authoredAudioTracks: authoredTracks.length,
            authoredAudioSpans: authoredSpanCount,
        },
        clips,
        boundaries,
        features: {
            sourceVideoClipNumbers,
            sourceVideoOnlyClipNumbers,
            upscaledStageCount,
            icLoraCount,
            loraCount,
            majorPromptClipNumbers,
            majorPromptOverrideClipNumbers,
            majorPromptInheritedClipNumbers,
            relayPromptCount,
            retakeClipNumbers,
            references,
            audio: {
                clips: audioClips,
                segmentCount,
                lengthFromAudioClipNumbers,
                lengthFromControlNetClipNumbers,
                authoredTracks,
            },
        },
        labels,
    };
};
