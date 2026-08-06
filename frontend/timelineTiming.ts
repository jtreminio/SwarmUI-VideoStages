import type { CapabilityViewResolver } from "./architectures/policy";
import { boundaryPlanForClips } from "./boundaryPlan";
import { executableBoundaries, executableClipIndexes } from "./clipSemantics";
import { framesForClip, NEUTRAL_FRAME_GRID } from "./renderUtils";
import { safeFps } from "./timelineDetail";
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
    clipFrames: number[];
    boundaries: TimelineBoundaryImpact[];
    authoredSeconds: number;
    generatedFrames: number;
    joinFrames: number;
    joinSeconds: number;
    outputFrames: number;
    outputSeconds: number;
    /** True when every authored card can live directly on the compacted output ruler. */
    outputGeometryAvailable: boolean;
}

/**
 * Mirrors the compiled timeline's frame accounting:
 *
 * output = generated clip frames - resolved pixel overlaps.
 *
 * Capability views are required for architecture-specific details such as
 * LTX Continue's extra continuity frame and unsupported-mode fallbacks.
 */
export const resolveTimelineTiming = (
    clips: readonly Clip[],
    rawFps: number,
    capabilities?: CapabilityViewResolver,
): TimelineTiming => {
    const fps = safeFps(rawFps);
    const indexes = executableClipIndexes(clips);
    const executable = new Set(indexes);
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
    const seams = executableBoundaries(clips);
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
        indexes.reduce((sum, clipIdx) => sum + clipFrames[clipIdx], 0) +
        boundaries.reduce((sum, boundary) => sum + boundary.handleFrames, 0);
    const joinFrames = boundaries.reduce(
        (sum, boundary) => sum + boundary.overlapFrames,
        0,
    );
    const outputFrames = Math.max(0, generatedFrames - joinFrames);
    return {
        fps,
        executableClipIndexes: indexes,
        clipFrames,
        boundaries,
        authoredSeconds: indexes.reduce(
            (sum, clipIdx) => sum + Math.max(0, clips[clipIdx].duration || 0),
            0,
        ),
        generatedFrames,
        joinFrames,
        joinSeconds: joinFrames / fps,
        outputFrames,
        outputSeconds: outputFrames / fps,
        outputGeometryAvailable: indexes.length === clips.length,
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

/**
 * The editable ruler keeps dormant cards reachable. When every card executes,
 * it is the compacted output ruler; otherwise it preserves authored card
 * geometry and marks the earlier output endpoint separately.
 */
export const timelineDisplaySeconds = (
    clips: readonly Clip[],
    timing: TimelineTiming,
): number =>
    timing.outputGeometryAvailable
        ? timing.outputSeconds
        : clips.reduce((sum, clip) => sum + Math.max(0, clip.duration || 0), 0);
