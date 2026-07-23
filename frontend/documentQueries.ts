import { safeFps } from "./timelineDetail";
import type { VideoStagesConfig } from "./types";

/** Canonical document FPS for timeline math, including the shared fallback. */
export const documentFps = (document: Pick<VideoStagesConfig, "fps">): number =>
    safeFps(document.fps);
