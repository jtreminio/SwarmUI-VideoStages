import { createCapabilityViewResolver } from "../architectures/policy";
import { plural } from "./formatters";
import type { ProjectedClipEntry } from "./projectionTypes";
import type { VideoBoundarySummary, VideoExecutionContext } from "./types";

const isInitialGuidance = (
    reference: ProjectedClipEntry["clip"]["refs"][number],
): boolean =>
    reference.fromEnd !== true &&
    Math.max(1, Math.round(reference.frame)) === 1;

export const projectBoundaries = (
    activeEntries: readonly ProjectedClipEntry[],
    context: VideoExecutionContext,
): VideoBoundarySummary[] => {
    const capabilityResolver = context.catalog
        ? createCapabilityViewResolver(context.catalog)
        : null;
    return activeEntries.slice(0, -1).map((left, index) => {
        const right = activeEntries[index + 1] as ProjectedClipEntry;
        const requested = left.clip.boundaryOut;
        let effective = requested;
        let fallback: VideoBoundarySummary["fallback"] = "none";
        const descriptor = context.catalog?.architectures.find(
            (entry) => entry.id === left.clip.architecture,
        );
        const constraints = descriptor?.boundaryRules[requested]?.constraints;
        if (capabilityResolver) {
            effective = capabilityResolver
                .forBoundary(left.clip, right.clip, left.index, right.index)
                .effective(requested);
        } else if (left.clip.architecture !== right.clip.architecture) {
            effective = "cut";
        }
        if (effective === "cut" && requested !== "cut") {
            fallback =
                left.clip.architecture !== right.clip.architecture
                    ? "cross-architecture"
                    : constraints?.targetRequiresGeneratedEntry === true &&
                        right.clip.sourceVideo !== null
                      ? "target-is-sourced-video"
                      : constraints?.targetRequiresStage === true &&
                          right.stages.length === 0
                        ? "target-has-no-stage"
                        : constraints?.targetDisallowsInitialReference ===
                                true && right.clip.refs.some(isInitialGuidance)
                          ? "target-has-first-frame-reference"
                          : "architecture-rule";
        }
        const overlapFrames =
            effective === "cut" ? 0 : left.clip.boundaryOutOverlap;
        const carryAudio =
            effective !== "cut" &&
            left.clip.boundaryOutCarryAudio === true &&
            right.stages.length > 0;
        let title =
            effective === "continue" && overlapFrames > 0
                ? `Continue (${plural(overlapFrames, "frame")} overlap)`
                : effective[0].toUpperCase() + effective.slice(1);
        if (carryAudio) {
            title += " + generated audio continuation";
        }
        const fallbackDescription =
            fallback === "target-is-sourced-video"
                ? "next clip is sourced footage"
                : fallback === "target-has-no-stage"
                  ? "next clip has no generation stage"
                  : fallback === "target-has-first-frame-reference"
                    ? "next clip has a first-frame reference"
                    : fallback === "cross-architecture"
                      ? "clips use different video architectures"
                      : fallback === "architecture-rule"
                        ? "the selected architecture rule does not allow this join"
                        : "";
        const result =
            fallback === "none"
                ? title
                : `${requested} → cut (${fallbackDescription})`;
        return {
            leftClipNumber: left.index,
            rightClipNumber: right.index,
            kind: requested,
            requested,
            effective,
            fallback,
            overlapFrames,
            carryAudio,
            label: `Clip ${left.index} → ${right.index}: ${result}`,
        };
    });
};
