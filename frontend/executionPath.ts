import {
    type AudioTrack,
    type AudioTrackSpan,
    type BoundaryOut,
    type Clip,
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
    type RefImage,
    type VideoStagesConfig,
} from "./types";

/** The host's starting material, deliberately limited to LTX-supported paths. */
export type LtxHostEntryHint =
    | "text-to-video"
    | "host-image-guidance"
    | "init-image-guidance"
    | "source-video"
    | "source-video-only"
    | "global-refine-video";

export interface LtxExecutionContext {
    /**
     * Explicitly identifies the separate Refine Video action, or overrides
     * normal clip-zero inference for callers that already know the entry path.
     */
    entryPoint?: LtxHostEntryHint;
    /**
     * How an otherwise generated clip zero entered VideoStages. Source video
     * and initial-image guidance are still derived from clip zero itself.
     */
    generatedEntry?: Extract<
        LtxHostEntryHint,
        "text-to-video" | "host-image-guidance"
    >;
    /**
     * The global Refine Video path normalizes these authored clip-zero stages
     * to control-only passes, including an effective upscale of 1×.
     */
    refineSkipStages?: number;
    /** The host prompt inherited by clips without a clip-local major prompt. */
    globalPrompt?: string;
}

export type LtxTimelineShape =
    | "no-executable-clips"
    | "single-clip-no-stage"
    | "single-clip-single-stage"
    | "single-clip-multi-stage"
    | "multi-clip-no-stage"
    | "multi-clip-mixed-stages"
    | "multi-clip-single-stage-each"
    | "multi-clip-multi-stage";

export type LtxClipExecutionKind =
    | "skipped"
    | "generated"
    | "source-video"
    | "source-video-only";

export interface LtxReferenceSummary {
    clipNumber: number;
    frame: number;
    fromEnd: boolean;
    source: string;
    label: string;
}

export interface LtxBoundarySummary {
    leftClipNumber: number;
    rightClipNumber: number;
    /** @deprecated Prefer `requested`; retained for compatibility. */
    kind: BoundaryOut;
    requested: BoundaryOut;
    effective: BoundaryOut;
    fallback:
        | "none"
        | "target-is-sourced-video"
        | "target-has-no-stage"
        | "target-has-first-frame-reference";
    overlapFrames: number;
    label: string;
}

export interface LtxClipAudioSummary {
    clipNumber: number;
    source: string;
    label: string;
    lengthFromAudio: boolean;
    lengthFromControlNet: boolean;
    reusesStageAudio: boolean;
    savesTrack: boolean;
    segmentCount: number;
}

export interface LtxClipPathSummary {
    clipNumber: number;
    kind: LtxClipExecutionKind;
    stageCount: number;
    activeStageCount: number;
    label: string;
}

export interface LtxAuthoredAudioSpanSummary {
    trackId: string;
    spanId: string;
    firstClipNumber: number | null;
    lastClipNumber: number | null;
    clipNumbers: number[];
    pending: boolean;
    label: string;
}

export interface LtxAuthoredAudioTrackSummary {
    trackId: string;
    sourceKind: string;
    sourceReference: string;
    sourceUploadFileName: string | null;
    spanCount: number;
    clipNumbers: number[];
    pendingSpanCount: number;
    spans: LtxAuthoredAudioSpanSummary[];
    label: string;
}

export interface LtxExecutionPathSummary {
    engine: "LTX Video";
    hostEntry: { kind: LtxHostEntryHint; label: string };
    shape: { kind: LtxTimelineShape; label: string };
    counts: {
        clips: number;
        executableClips: number;
        stages: number;
        activeStages: number;
        authoredAudioTracks: number;
        authoredAudioSpans: number;
    };
    clips: LtxClipPathSummary[];
    boundaries: LtxBoundarySummary[];
    features: {
        sourceVideoClipNumbers: number[];
        sourceVideoOnlyClipNumbers: number[];
        upscaledStageCount: number;
        icLoraCount: number;
        loraCount: number;
        majorPromptClipNumbers: number[];
        majorPromptOverrideClipNumbers: number[];
        majorPromptInheritedClipNumbers: number[];
        relayPromptCount: number;
        retakeClipNumbers: number[];
        references: LtxReferenceSummary[];
        audio: {
            clips: LtxClipAudioSummary[];
            segmentCount: number;
            lengthFromAudioClipNumbers: number[];
            lengthFromControlNetClipNumbers: number[];
            authoredTracks: LtxAuthoredAudioTrackSummary[];
        };
    };
    /** Compact, display-ready labels; deliberately avoids implementation nodes. */
    labels: string[];
}

const hostEntryLabel: Record<LtxHostEntryHint, string> = {
    "text-to-video": "Text-to-video",
    "host-image-guidance": "Host image guidance",
    "init-image-guidance": "User-provided init image guidance",
    "source-video": "User-provided source video",
    "source-video-only": "Source video without generation stages",
    "global-refine-video": "Refine an existing video",
};

const plural = (
    count: number,
    singular: string,
    pluralWord = `${singular}s`,
): string => `${count} ${count === 1 ? singular : pluralWord}`;

interface ProjectedStage {
    stage: Clip["stages"][number];
    rawIndex: number;
}

const projectedStages = (
    clip: Clip,
    clipIndex: number,
    context: LtxExecutionContext,
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

const activeStages = (clip: Clip) =>
    clip.stages.filter((stage) => stage.skipped !== true);

const isExecutable = (clip: Clip, stages: readonly ProjectedStage[]): boolean =>
    clip.skipped !== true && (clip.sourceVideo !== null || stages.length > 0);

const referenceSourceLabel = (reference: RefImage): string => {
    if (reference.source === REF_SOURCE_UPLOAD) {
        return "uploaded image";
    }
    if (reference.source === REF_SOURCE_BASE) {
        return "host image";
    }
    if (reference.source === REF_SOURCE_REFINER) {
        return "refiner image";
    }
    return reference.source || "image";
};

const audioSourceLabel = (source: string): string => {
    const trimmed = `${source ?? ""}`.trim();
    const aceMatch = /^audio(\d+)$/i.exec(trimmed);
    if (aceMatch) {
        return `AceStepFun track ${aceMatch[1]}`;
    }
    return trimmed || "Native";
};

const isInitialGuidance = (reference: RefImage): boolean =>
    reference.fromEnd !== true &&
    Math.max(1, Math.round(reference.frame)) === 1;

const inferHostEntry = (
    clips: readonly Clip[],
    context: LtxExecutionContext,
): LtxHostEntryHint => {
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
    if (initialReferences.length > 0) {
        return "host-image-guidance";
    }
    return "text-to-video";
};

const describeShape = (
    stageCounts: readonly number[],
): { kind: LtxTimelineShape; label: string } => {
    if (stageCounts.length === 0) {
        return { kind: "no-executable-clips", label: "No executable clips" };
    }
    if (stageCounts.length === 1) {
        if (stageCounts[0] === 0) {
            return {
                kind: "single-clip-no-stage",
                label: "Single clip · no generation stages",
            };
        }
        return stageCounts[0] === 1
            ? {
                  kind: "single-clip-single-stage",
                  label: "Single clip · single stage",
              }
            : {
                  kind: "single-clip-multi-stage",
                  label: "Single clip · multi-stage",
              };
    }
    if (stageCounts.every((count) => count === 0)) {
        return {
            kind: "multi-clip-no-stage",
            label: "Multiple clips · no generation stages",
        };
    }
    if (stageCounts.some((count) => count === 0)) {
        const categories = [
            "source-only",
            ...(stageCounts.some((count) => count === 1)
                ? ["single-stage"]
                : []),
            ...(stageCounts.some((count) => count > 1) ? ["multi-stage"] : []),
        ];
        return {
            kind: "multi-clip-mixed-stages",
            label: `Multiple clips · mixed ${categories.join(", ")}`,
        };
    }
    return stageCounts.every((count) => count === 1)
        ? {
              kind: "multi-clip-single-stage-each",
              label: "Multiple clips · single stage each",
          }
        : {
              kind: "multi-clip-multi-stage",
              label: "Multiple clips · multi-stage",
          };
};

const describeClip = (
    clip: Clip,
    clipNumber: number,
    effectiveStages: readonly ProjectedStage[],
): LtxClipPathSummary => {
    const activeStageCount = effectiveStages.length;
    const stageCount = clip.stages.length;
    if (clip.skipped) {
        return {
            clipNumber,
            kind: "skipped",
            stageCount,
            activeStageCount,
            label: `Clip ${clipNumber}: skipped`,
        };
    }
    if (clip.sourceVideo && activeStageCount === 0) {
        return {
            clipNumber,
            kind: "source-video-only",
            stageCount,
            activeStageCount,
            label: `Clip ${clipNumber}: source video only`,
        };
    }
    if (clip.sourceVideo) {
        return {
            clipNumber,
            kind: "source-video",
            stageCount,
            activeStageCount,
            label: `Clip ${clipNumber}: source video + ${plural(activeStageCount, "active stage")}`,
        };
    }
    return {
        clipNumber,
        kind: "generated",
        stageCount,
        activeStageCount,
        label: `Clip ${clipNumber}: generated + ${plural(activeStageCount, "active stage")}`,
    };
};

const describeAuthoredAudioSpan = (
    track: AudioTrack,
    span: AudioTrackSpan,
    spanIndex: number,
    clipIndexById: ReadonlyMap<string, number>,
    clipCount: number,
): LtxAuthoredAudioSpanSummary => {
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

const describeAuthoredAudioTracks = (
    config: VideoStagesConfig,
): LtxAuthoredAudioTrackSummary[] => {
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

/**
 * Projects the persisted editor state into a small LTX-only explanation of
 * what the user will make. It intentionally describes paths and options, not
 * backend graph nodes or execution internals.
 */
export const projectLtxExecutionPath = (
    config: VideoStagesConfig,
    context: LtxExecutionContext = {},
): LtxExecutionPathSummary => {
    const activeEntries = config.clips
        .map((clip, index) => ({
            clip,
            index,
            stages: projectedStages(clip, index, context),
        }))
        .filter(({ clip, stages }) => isExecutable(clip, stages));
    const executable = activeEntries.map(({ clip }) => clip);
    const clips = config.clips.map((clip, index) =>
        describeClip(clip, index + 1, projectedStages(clip, index, context)),
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
    const boundaries = activeEntries.slice(0, -1).map((left, index) => {
        const right = activeEntries[
            index + 1
        ] as (typeof activeEntries)[number];
        const requested = left.clip.boundaryOut;
        let effective = requested;
        let fallback: LtxBoundarySummary["fallback"] = "none";
        if (requested === "continue" && right.clip.sourceVideo !== null) {
            effective = "cut";
            fallback = "target-is-sourced-video";
        } else if (requested === "continue" && right.stages.length === 0) {
            // Kept in parity with the backend planner. This branch is normally
            // unreachable after executable compaction because an unsourced
            // zero-stage clip is not executable.
            effective = "cut";
            fallback = "target-has-no-stage";
        } else if (
            requested === "continue" &&
            right.clip.refs.some(isInitialGuidance)
        ) {
            effective = "cut";
            fallback = "target-has-first-frame-reference";
        }
        const overlapFrames =
            effective === "cut" ? 0 : left.clip.boundaryOutOverlap;
        const title =
            effective === "continue" && overlapFrames > 0
                ? `Continue (${plural(overlapFrames, "frame")} overlap)`
                : effective[0].toUpperCase() + effective.slice(1);
        const fallbackDescription =
            fallback === "target-is-sourced-video"
                ? "next clip is sourced footage"
                : fallback === "target-has-no-stage"
                  ? "next clip has no generation stage"
                  : fallback === "target-has-first-frame-reference"
                    ? "next clip has a first-frame reference"
                    : "";
        const result =
            fallback === "none"
                ? title
                : `${requested} → cut (${fallbackDescription})`;
        return {
            leftClipNumber: left.index + 1,
            rightClipNumber: right.index + 1,
            kind: requested,
            requested,
            effective,
            fallback,
            overlapFrames,
            label: `Clip ${left.index + 1} → ${right.index + 1}: ${result}`,
        };
    });
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
    const audioClips = activeEntries.map(({ clip, index, stages }) => ({
        clipNumber: index + 1,
        source: clip.audioSource,
        label: `Clip ${index + 1}: ${audioSourceLabel(clip.audioSource)} audio`,
        lengthFromAudio: clip.clipLengthFromAudio === true,
        lengthFromControlNet: clip.clipLengthFromControlNet === true,
        reusesStageAudio: clip.reuseAudio === true && stages.length >= 3,
        savesTrack: clip.saveAudioTrack === true,
        segmentCount: clip.audioSegments.length,
    }));
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
    const authoredTracks = describeAuthoredAudioTracks(config);
    const authoredSpanCount = authoredTracks.reduce(
        (total, track) => total + track.spanCount,
        0,
    );
    const pendingAuthoredSpanCount = authoredTracks.reduce(
        (total, track) => total + track.pendingSpanCount,
        0,
    );
    const labels = ["LTX Video", hostEntryLabel[hostEntry], shape.label];
    if (sourceVideoClipNumbers.length > 0) {
        labels.push(
            `${plural(sourceVideoClipNumbers.length, "source-video clip")}`,
        );
    }
    if (sourceVideoOnlyClipNumbers.length > 0) {
        labels.push(
            `${plural(sourceVideoOnlyClipNumbers.length, "source-video-only clip")}`,
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
    if (icLoraCount > 0) {
        labels.push(`${plural(icLoraCount, "IC-LoRA")}`);
    }
    if (loraCount > 0) {
        labels.push(`${plural(loraCount, "LoRA")}`);
    }
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
        labels.push(`${plural(relayPromptCount, "relay prompt")}`);
    }
    if (retakeClipNumbers.length > 0) {
        labels.push(`${plural(retakeClipNumbers.length, "retake")}`);
    }
    if (references.length > 0) {
        labels.push(`${plural(references.length, "frame reference")}`);
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
    if (segmentCount > 0) {
        labels.push(`${plural(segmentCount, "audio segment")}`);
    }
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
        labels.push(`${plural(savedTrackCount, "saved audio output")}`);
    }
    if (authoredTracks.length > 0) {
        labels.push(
            `${plural(authoredTracks.length, "planned audio track")} · ${plural(authoredSpanCount, "span")}${pendingAuthoredSpanCount > 0 ? ` · ${plural(pendingAuthoredSpanCount, "pending span")}` : ""}`,
        );
    }

    return {
        engine: "LTX Video",
        hostEntry: { kind: hostEntry, label: hostEntryLabel[hostEntry] },
        shape,
        counts: {
            clips: config.clips.length,
            executableClips: executable.length,
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
