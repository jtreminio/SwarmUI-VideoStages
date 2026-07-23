import { getLtxHostBridge } from "./host";
import { readProp } from "./normalization";
import { getState } from "./persistence";
import { isVideoStagesEnabled } from "./swarmInputs";
import type { VideoStagesConfig } from "./types";
import { isRecord, safeJsonParse } from "./utils";

export const refineNeedsExtraStageMessage = (skipCount: number): string =>
    `Refine Video needs Clip 1 to have at least one active stage after Stage ${skipCount - 1} ` +
    `(for example, an upscale or refine stage). Add a stage in the VideoStages panel, then click Refine Video again.`;

export const countActiveStagesInMetadataClip0 = (
    videostagesJson: string,
): number => {
    const parsed = safeJsonParse<unknown>(videostagesJson, null);
    if (!isRecord(parsed)) {
        return 0;
    }
    const clips = readProp(parsed, "clips");
    if (!Array.isArray(clips) || clips.length === 0) {
        return 0;
    }
    const clip0 = clips[0];
    if (!isRecord(clip0) || readProp(clip0, "skipped") === true) {
        return 0;
    }
    const stages = readProp(clip0, "stages");
    if (!Array.isArray(stages)) {
        return 0;
    }
    return stages.filter(
        (stage) => !(isRecord(stage) && readProp(stage, "skipped") === true),
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
    const description =
        "Re-runs VideoStages using this video as the source for Clip 1 (skips the first N stage samplers, " +
        "where N is read from the source video's metadata). Requires an extra stage beyond those.";
    getLtxHostBridge().registerRefineVideoButton((src: string): void => {
        const host = getLtxHostBridge();
        const run = async (): Promise<void> => {
            let parsedMetadata: unknown = null;
            const currentMetadata = host.getCurrentMediaMetadata();
            if (currentMetadata) {
                try {
                    const readable =
                        host.interpretMediaMetadata(currentMetadata);
                    parsedMetadata = readable ? JSON.parse(readable) : null;
                } catch (error) {
                    console.warn(
                        "VideoStages: failed to parse source video metadata",
                        error,
                    );
                }
            }

            const params = isRecord(parsedMetadata)
                ? readProp(parsedMetadata, "sui_image_params")
                : null;
            const sourceVideostages = isRecord(params)
                ? readProp(params, "videostages")
                : undefined;
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
                host.showError(refineNeedsExtraStageMessage(skipCount));
                return;
            }

            const videoDataUrl = await host.toDataUrl(src);
            const inputOverrides: Record<string, unknown> = {
                videostagesrefinesourcevideo: videoDataUrl,
                videostagesrefineskipstages: skipCount,
                images: 1,
            };

            const seed = isRecord(params)
                ? readProp(params, "seed")
                : undefined;
            if (typeof seed === "number") {
                inputOverrides.seed = seed;
            }

            host.generate(inputOverrides);
        };
        void run();
    }, description);
};
