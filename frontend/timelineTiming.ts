import type { CapabilityViewResolver } from "./architectures/policy";
import { crossfadePlanForClips } from "./boundaryPlan";
import { executableBoundaries, executableClipIndexes } from "./clipSemantics";
import { framesForClip } from "./renderUtils";
import { safeFps } from "./timelineDetail";
import type { BoundaryOut, Clip } from "./types";

export interface TimelineBoundaryImpact {
    position: number;
    leftIdx: number;
    rightIdx: number;
    requestedMode: BoundaryOut;
    effectiveMode: BoundaryOut;
    overlapFrames: number;
    overlapSeconds: number;
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
 * output = executable clip frames - resolved boundary windows.
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
        const effective =
            position < indexes.length - 1
                ? (capabilities
                      ?.forBoundaryIndex(clips, clipIdx)
                      .effective(requested) ?? requested)
                : "cut";
        return { ...clip, boundaryOut: effective };
    });
    const plan = capabilities
        ? crossfadePlanForClips(
              compacted,
              fps,
              (_left, position, mode) =>
                  capabilities
                      .forBoundaryIndex(clips, indexes[position])
                      .overlapConstraints(mode),
              (clip) => capabilities.forClip(clip).frameGrid,
          )
        : crossfadePlanForClips(compacted, fps);
    const seams = executableBoundaries(clips);
    const boundaries = seams.map((seam) => {
        const requestedMode = clips[seam.leftIdx].boundaryOut ?? "cut";
        const policyEffective =
            capabilities
                ?.forBoundaryIndex(clips, seam.leftIdx)
                .effective(requestedMode) ?? requestedMode;
        const overlapFrames = Math.max(0, plan.overlaps[seam.position] ?? 0);
        return {
            ...seam,
            requestedMode,
            effectiveMode:
                overlapFrames > 0 ? policyEffective : ("cut" as const),
            overlapFrames,
            overlapSeconds: overlapFrames / fps,
        };
    });
    const clipFrames = clips.map((clip, clipIdx) =>
        executable.has(clipIdx)
            ? framesForClip(
                  clip.duration,
                  fps,
                  capabilities?.forClip(clip).frameGrid ?? 1,
              )
            : 0,
    );
    const generatedFrames = indexes.reduce(
        (sum, clipIdx) => sum + clipFrames[clipIdx],
        0,
    );
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
