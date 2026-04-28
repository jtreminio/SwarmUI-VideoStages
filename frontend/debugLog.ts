/**
 * Trace VideoStages reactions (saves, refreshes, extension events).
 * In DevTools: `window.__VIDEO_STAGES_DEBUG__ = true` then filter console by `[VideoStages debug]`.
 */
export const videoStagesDebugEnabled = (): boolean =>
    typeof window !== "undefined" && !!window.__VIDEO_STAGES_DEBUG__;

export const videoStagesDebugLog = (
    area: string,
    message: string,
    ...details: unknown[]
): void => {
    if (!videoStagesDebugEnabled()) {
        return;
    }
    console.debug(`[VideoStages debug ${area}]`, message, ...details);
};
