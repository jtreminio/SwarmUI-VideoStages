import { reconcileClipArchitectureIdentity } from "./architectures/clipIdentity";
import { captureAuthoringTransactionSnapshot } from "./authoringSnapshot";
import { applySkipSuffix } from "./clipSemantics";
import { getVideoStagesHostBridge } from "./host";
import type { InitVideoProbe } from "./mediaProbe";
import { initVideoFromProbe, probeInitVideo } from "./mediaProbe";
import { readProp } from "./normalizationShared";
import { serializeStateForStorage } from "./persistence/documentCodec";
import { getState } from "./persistence/repository";
import { isVideoStagesEnabled } from "./swarmInputs";
import type { AuthoringDocument, Clip } from "./types";
import { isRecord } from "./utils";

const REFINE_SOURCE_FILE_NAME = "refine-source";
const REFINE_STAGE_INDEX = 1;

export const refineNeedsExtraStageMessage = (): string =>
    `Refine Video needs Clip 0 to have a Stage ${REFINE_STAGE_INDEX} defined ` +
    `(for example, an upscale or refine stage). Add a stage in the VideoStages panel, then click Refine Video again.`;

export const hasRefinementWorkToDo = (
    state: AuthoringDocument,
    enabled: boolean,
): boolean => {
    if (!enabled) {
        return false;
    }
    const clip0 = state.clips[0];
    if (!clip0 || clip0.skipped) {
        return false;
    }
    return clip0.stages.length > REFINE_STAGE_INDEX;
};

/**
 * Already-generated stages become passthroughs; skipping them would truncate refinement.
 */
export const applyRefineToClipZero = (
    clip: Clip,
    data: string,
    probe: InitVideoProbe | null,
): void => {
    clip.initVideo = initVideoFromProbe(
        probe,
        data,
        REFINE_SOURCE_FILE_NAME,
        clip.duration,
    );
    if (clip.stages.length > REFINE_STAGE_INDEX) {
        applySkipSuffix(clip.stages, REFINE_STAGE_INDEX, false);
    }
    if (clip.stages[0]) {
        clip.stages[0].control = 0;
    }
};

export const refineVideoButton = (): void => {
    const description =
        "Re-runs VideoStages using this video as Clip 0's source, passes through Stage 0, and runs Stage 1.";
    getVideoStagesHostBridge().registerRefineVideoButton(
        (src: string): void => {
            const host = getVideoStagesHostBridge();
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
                const initialState = getState();

                if (
                    !hasRefinementWorkToDo(initialState, isVideoStagesEnabled())
                ) {
                    host.showError(refineNeedsExtraStageMessage());
                    return;
                }

                const videoDataUrl = await host.toDataUrl(src);
                const probe = await probeInitVideo(videoDataUrl);
                // The author may edit the timeline while the video probe runs.
                const state = getState();
                const clipZero = state.clips[0];
                if (
                    !clipZero ||
                    !hasRefinementWorkToDo(state, isVideoStagesEnabled())
                ) {
                    host.showError(refineNeedsExtraStageMessage());
                    return;
                }
                const clips = [...state.clips];
                clips[0] = structuredClone(clipZero);
                applyRefineToClipZero(clips[0], videoDataUrl, probe);
                // Adding init video can invalidate the clip's architecture identity.
                reconcileClipArchitectureIdentity(
                    clips[0],
                    captureAuthoringTransactionSnapshot().capabilities.catalog,
                );
                const inputOverrides: Record<string, unknown> = {
                    videostages: serializeStateForStorage({ ...state, clips }),
                    images: 1,
                };

                const prompt = isRecord(params)
                    ? readProp(params, "prompt")
                    : undefined;
                if (typeof prompt === "string") {
                    inputOverrides.prompt = prompt;
                }

                const negativePrompt = isRecord(params)
                    ? readProp(params, "negativeprompt")
                    : undefined;
                if (typeof negativePrompt === "string") {
                    inputOverrides.negativeprompt = negativePrompt;
                }

                const seed = isRecord(params)
                    ? readProp(params, "seed")
                    : undefined;
                if (typeof seed === "number") {
                    inputOverrides.seed = seed;
                }

                const sourceExtraMetadata = isRecord(parsedMetadata)
                    ? readProp(parsedMetadata, "sui_extra_data")
                    : undefined;
                if (isRecord(sourceExtraMetadata)) {
                    inputOverrides.extra_metadata =
                        structuredClone(sourceExtraMetadata);
                }

                host.generate(inputOverrides);
            };
            void run();
        },
        description,
    );
};
