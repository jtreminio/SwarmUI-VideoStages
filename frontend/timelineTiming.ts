import type { CapabilityViewResolver } from "./architectures/policy";
import { boundaryPlanForClips } from "./boundaryPlan";
import { framesForClip, NEUTRAL_FRAME_GRID } from "./renderUtils";
import { safeFps } from "./timelineDetail";
import {
    resolveTimelinePublication,
    type TimelineDependency,
} from "./timelinePublication";
import type { BoundaryOut, Clip } from "./types";

export interface TimelineBoundaryImpact {
    position: number;
    leftIdx: number;
    rightIdx: number;
    requestedMode: BoundaryOut;
    effectiveMode: BoundaryOut;
    continuityWindowFrames: number;
    continuityWindowSeconds: number;
    overlapFrames: number;
    overlapSeconds: number;
    handleFrames: number;
    handleSeconds: number;
    timelineReductionFrames: number;
    timelineReductionSeconds: number;
}

export interface TimelineTiming {
    fps: number;
    executableClipIndexes: number[];
    outputClipIndexes: number[];
    consumedClipIndexes: number[];
    dependencies: TimelineDependency[];
    clipFrames: number[];
    boundaries: TimelineBoundaryImpact[];
    authoredSeconds: number;
    generatedFrames: number;
    joinFrames: number;
    joinSeconds: number;
    outputFrames: number;
    outputSeconds: number;
    /** True when published cards can use compacted output geometry. */
    outputGeometryAvailable: boolean;
}

/** Mirrors the runtime's published clips, then applies their resolved joins. */
export const resolveTimelineTiming = (
    clips: readonly Clip[],
    rawFps: number,
    capabilities?: CapabilityViewResolver,
): TimelineTiming => {
    const fps = safeFps(rawFps);
    const publication = resolveTimelinePublication(clips);
    const executable = new Set(publication.executableClipIndexes);
    const indexes = publication.outputClipIndexes;
    const compacted = indexes.map((clipIdx, position) => {
        const clip = clips[clipIdx];
        const requested = clip.boundaryOut ?? "cut";
        let effective =
            position < indexes.length - 1
                ? (capabilities
                      ?.forBoundaryIndex(clips, clipIdx)
                      .effective(requested) ?? requested)
                : "cut";
        const target = clips[indexes[position + 1]];
        if (
            effective === "continue" &&
            (target?.clipLengthFromAudio === true ||
                target?.clipLengthFromControlNet === true)
        ) {
            effective = "cut";
        }
        return { ...clip, boundaryOut: effective };
    });
    const plan = capabilities
        ? boundaryPlanForClips(
              compacted,
              fps,
              (_left, position, mode) =>
                  capabilities
                      .forBoundaryIndex(clips, indexes[position])
                      .windowConstraints(mode),
              (clip) => capabilities.forClip(clip).frameGrid,
          )
        : boundaryPlanForClips(compacted, fps);
    const seams = indexes.slice(0, -1).map((leftIdx, position) => ({
        position,
        leftIdx,
        rightIdx: indexes[position + 1],
        leftId: clips[leftIdx]?.id,
        rightId: clips[indexes[position + 1]]?.id,
    }));
    const boundaries = seams.map((seam) => {
        const requestedMode = clips[seam.leftIdx].boundaryOut ?? "cut";
        const policyEffective = compacted[seam.position].boundaryOut ?? "cut";
        const overlapFrames = Math.max(0, plan.overlaps[seam.position] ?? 0);
        const continuityWindowFrames = Math.max(
            0,
            plan.continuityWindows[seam.position] ?? 0,
        );
        const effectiveMode =
            overlapFrames > 0 || continuityWindowFrames > 0
                ? policyEffective
                : ("cut" as const);
        const continuityExtraFrames =
            effectiveMode === "continue"
                ? (capabilities
                      ?.forBoundaryIndex(clips, seam.leftIdx)
                      .windowConstraints(effectiveMode).continuityExtraFrames ??
                  1)
                : 0;
        const handleFrames =
            effectiveMode === "continue"
                ? Math.max(0, overlapFrames - continuityExtraFrames)
                : 0;
        const timelineReductionFrames = Math.max(
            0,
            overlapFrames - handleFrames,
        );
        return {
            ...seam,
            requestedMode,
            effectiveMode,
            continuityWindowFrames,
            continuityWindowSeconds: continuityWindowFrames / fps,
            overlapFrames,
            overlapSeconds: overlapFrames / fps,
            handleFrames,
            handleSeconds: handleFrames / fps,
            timelineReductionFrames,
            timelineReductionSeconds: timelineReductionFrames / fps,
        };
    });
    const clipFrames = clips.map((clip, clipIdx) =>
        executable.has(clipIdx)
            ? framesForClip(
                  clip.duration,
                  fps,
                  capabilities?.forClip(clip).frameGrid ?? NEUTRAL_FRAME_GRID,
              )
            : 0,
    );
    const generatedFrames =
        publication.executableClipIndexes.reduce(
            (sum, clipIdx) => sum + clipFrames[clipIdx],
            0,
        ) +
        boundaries.reduce((sum, boundary) => sum + boundary.handleFrames, 0);
    const outputGeneratedFrames =
        indexes.reduce((sum, clipIdx) => sum + clipFrames[clipIdx], 0) +
        boundaries.reduce((sum, boundary) => sum + boundary.handleFrames, 0);
    const joinFrames = boundaries.reduce(
        (sum, boundary) => sum + boundary.overlapFrames,
        0,
    );
    const outputFrames = Math.max(0, outputGeneratedFrames - joinFrames);
    return {
        fps,
        executableClipIndexes: publication.executableClipIndexes,
        outputClipIndexes: publication.outputClipIndexes,
        consumedClipIndexes: publication.consumedClipIndexes,
        dependencies: publication.dependencies,
        clipFrames,
        boundaries,
        authoredSeconds: publication.executableClipIndexes.reduce(
            (sum, clipIdx) => sum + Math.max(0, clips[clipIdx].duration || 0),
            0,
        ),
        generatedFrames,
        joinFrames,
        joinSeconds: joinFrames / fps,
        outputFrames,
        outputSeconds: outputFrames / fps,
        outputGeometryAvailable:
            publication.consumedClipIndexes.length > 0 ||
            publication.executableClipIndexes.length === clips.length,
    };
};

export const boundaryImpactForLeftClip = (
    timing: TimelineTiming,
    leftClipIdx: number,
): TimelineBoundaryImpact | null =>
    timing.boundaries.find((boundary) => boundary.leftIdx === leftClipIdx) ??
    null;

export const incomingReferenceContinueForClip = (
    clips: readonly Clip[],
    rawFps: number,
    capabilities: CapabilityViewResolver,
    rightClipIdx: number,
): TimelineBoundaryImpact | null =>
    resolveTimelineTiming(clips, rawFps, capabilities).boundaries.find(
        (boundary) =>
            boundary.rightIdx === rightClipIdx &&
            boundary.effectiveMode === "continue" &&
            capabilities
                .forBoundaryIndex(clips, boundary.leftIdx)
                .windowConstraints("continue").continueMode === "reference",
    ) ?? null;

/** Dormant cards keep the authored ruler; dependency flows use published time. */
export const timelineDisplaySeconds = (
    clips: readonly Clip[],
    timing: TimelineTiming,
): number => {
    if (timing.consumedClipIndexes.length > 0) {
        const executable = new Set(timing.executableClipIndexes);
        const dormantSeconds = clips.reduce(
            (sum, clip, index) =>
                executable.has(index)
                    ? sum
                    : sum + Math.max(0, clip.duration || 0),
            0,
        );
        return timing.outputSeconds + dormantSeconds;
    }
    return timing.outputGeometryAvailable
        ? timing.outputSeconds
        : clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);
};
