import { createDefaultVideoStagesHostBridge } from "./defaultVideoStagesHostBridge";
import type { VideoStagesHostBridge } from "./VideoStagesHostBridge";

let bridge: VideoStagesHostBridge = createDefaultVideoStagesHostBridge();

export const getVideoStagesHostBridge = (): VideoStagesHostBridge => bridge;

/** Test injection seam; pass null to restore the production bridge. */
export const setVideoStagesHostBridgeForTests = (
    replacement: VideoStagesHostBridge | null,
): void => {
    bridge = replacement ?? createDefaultVideoStagesHostBridge();
};

export type {
    HostOptionList,
    HostRegistrySnapshot,
    PromptPrefixExamples,
    VideoStagesHostBridge,
} from "./VideoStagesHostBridge";
