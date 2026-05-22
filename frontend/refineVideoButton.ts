import { getState } from "./persistence";
import { isVideoStagesEnabled } from "./swarmInputs";
import type { VideoStagesConfig } from "./types";

export const refineNeedsExtraStageMessage = (skipCount: number): string =>
    `Refine Video needs clip 0 to have at least one active stage after Stage ${skipCount - 1} ` +
    `(for example, an upscale or refine stage). Add a stage in the VideoStages panel, then click Refine Video again.`;

interface ParsedMetadata {
    sui_image_params?: {
        seed?: number;
        videostages?: string;
    };
}

export const countActiveStagesInMetadataClip0 = (
    videostagesJson: string,
): number => {
    let parsed: unknown;
    try {
        parsed = JSON.parse(videostagesJson);
    } catch {
        return 0;
    }
    if (!parsed || typeof parsed !== "object") {
        return 0;
    }
    const clips = (parsed as { clips?: unknown }).clips;
    if (!Array.isArray(clips) || clips.length === 0) {
        return 0;
    }
    const clip0 = clips[0] as { skipped?: unknown; stages?: unknown };
    if (clip0.skipped === true || !Array.isArray(clip0.stages)) {
        return 0;
    }
    return (clip0.stages as { skipped?: unknown }[]).filter(
        (stage) => stage.skipped !== true,
    ).length;
};

export const hasRefinementWorkToDo = (
    state: VideoStagesConfig,
    enabled: boolean,
    skipCount: number,
): boolean => {
    if (!enabled) {
        return false;
    }
    const clip0 = state.clips[0];
    if (!clip0 || clip0.skipped) {
        return false;
    }
    const activeStages = clip0.stages.filter((stage) => !stage.skipped);
    return activeStages.length > skipCount;
};

export const refineVideoButton = (): void => {
    if (typeof registerMediaButton !== "function") {
        return;
    }

    registerMediaButton(
        "Refine Video",
        (src: string): void => {
            let parsedMetadata: ParsedMetadata | null = null;
            if (currentMetadataVal) {
                try {
                    const readable = interpretMetadata(currentMetadataVal);
                    parsedMetadata = readable
                        ? (JSON.parse(readable) as ParsedMetadata)
                        : null;
                } catch (error) {
                    console.warn(
                        "VideoStages: failed to parse source video metadata",
                        error,
                    );
                }
            }

            const sourceVideostages =
                parsedMetadata?.sui_image_params?.videostages;
            const skipCount = Math.max(
                1,
                typeof sourceVideostages === "string"
                    ? countActiveStagesInMetadataClip0(sourceVideostages)
                    : 0,
            );

            if (
                !hasRefinementWorkToDo(
                    getState(),
                    isVideoStagesEnabled(),
                    skipCount,
                )
            ) {
                showError(refineNeedsExtraStageMessage(skipCount));
                return;
            }

            toDataURL(src, (videoDataUrl: string): void => {
                const inputOverrides: Record<string, unknown> = {
                    videostagesrefinesourcevideo: videoDataUrl,
                    videostagesrefineskipstages: skipCount,
                    images: 1,
                };

                const seed = parsedMetadata?.sui_image_params?.seed;
                if (typeof seed === "number") {
                    inputOverrides.seed = seed;
                }

                mainGenHandler.doGenerate(inputOverrides, {});
            });
        },
        "Re-runs VideoStages using this video as the source for clip 0 (skips the first N stage samplers, " +
            "where N is read from the source video's metadata). Requires an extra stage beyond those.",
        ["video"],
        true,
    );
};
