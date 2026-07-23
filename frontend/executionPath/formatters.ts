import { videoArchitectureRegistry } from "../architectures/registry";
import type { Clip, RefImage } from "../types";
import {
    REF_SOURCE_BASE,
    REF_SOURCE_REFINER,
    REF_SOURCE_UPLOAD,
} from "../types";
import type { ProjectedStage } from "./projectionTypes";
import type {
    VideoClipPathSummary,
    VideoExecutionContext,
    VideoHostEntryHint,
    VideoTimelineShape,
} from "./types";

export const HOST_ENTRY_LABEL: Record<VideoHostEntryHint, string> = {
    "text-to-video": "Text-to-video",
    "host-image-guidance": "Text → image → video",
    "init-image-guidance": "User-provided init image guidance",
    "source-video": "User-provided source video",
    "source-video-only": "Source video without generation stages",
    "global-refine-video": "Refine an existing video",
};

export const plural = (
    count: number,
    singular: string,
    pluralWord = `${singular}s`,
): string => `${count} ${count === 1 ? singular : pluralWord}`;

export const referenceSourceLabel = (reference: RefImage): string => {
    if (reference.source === REF_SOURCE_UPLOAD) return "uploaded image";
    if (reference.source === REF_SOURCE_BASE) return "host image";
    if (reference.source === REF_SOURCE_REFINER) return "refiner image";
    return reference.source || "image";
};

export const audioSourceLabel = (source: string): string => {
    const trimmed = `${source ?? ""}`.trim();
    const aceMatch = /^audio(\d+)$/i.exec(trimmed);
    return aceMatch ? `AceStepFun track ${aceMatch[1]}` : trimmed || "Native";
};

export const describeShape = (
    stageCounts: readonly number[],
): { kind: VideoTimelineShape; label: string } => {
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

export const describeClip = (
    clip: Clip,
    clipNumber: number,
    effectiveStages: readonly ProjectedStage[],
    context: VideoExecutionContext,
): VideoClipPathSummary => {
    const activeStageCount = effectiveStages.length;
    const architectureLabel =
        context.catalog?.architectures.find(
            (entry) => entry.id === clip.architecture,
        )?.label ??
        videoArchitectureRegistry.get(clip.architecture)?.label ??
        (clip.architecture === "none"
            ? "Source only"
            : clip.architecture || "Unknown architecture");
    const shared = {
        clipNumber,
        architectureId: clip.architecture,
        architectureLabel,
        modelProfileId: clip.modelProfileId,
        stageCount: clip.stages.length,
        activeStageCount,
    };
    if (clip.skipped) {
        return {
            ...shared,
            kind: "skipped",
            label: `Clip ${clipNumber}: skipped · ${architectureLabel}`,
        };
    }
    if (clip.sourceVideo && activeStageCount === 0) {
        return {
            ...shared,
            kind: "source-video-only",
            label: `Clip ${clipNumber}: source video only`,
        };
    }
    if (clip.sourceVideo) {
        return {
            ...shared,
            kind: "source-video",
            label: `Clip ${clipNumber}: source video + ${plural(activeStageCount, "active stage")} · ${architectureLabel}`,
        };
    }
    return {
        ...shared,
        kind: "generated",
        label: `Clip ${clipNumber}: generated + ${plural(activeStageCount, "active stage")} · ${architectureLabel}`,
    };
};
