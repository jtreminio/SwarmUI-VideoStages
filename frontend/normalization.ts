/**
 * Compatibility facade for normalization consumers. Domain implementation
 * lives in focused modules with one-way dependencies; keep this public surface
 * stable so existing imports do not need to know the decomposition.
 */
export {
    normalizeAudioSegments,
    normalizeAudioTrackSpan,
    normalizeAudioTracks,
} from "./normalizationAudio";
export {
    buildDefaultClip,
    normalizeBoundaryOut,
    normalizeClip,
    normalizeContinueOverlap,
} from "./normalizationClip";
export {
    normalizePromptWindows,
    normalizeRetake,
    normalizeSourceVideo,
    normalizeUploadedAudio,
} from "./normalizationMedia";
export { readProp } from "./normalizationShared";
export {
    appendRefToClip,
    buildDefaultRef,
    buildDefaultStage,
    buildDefaultStageRefStrengths,
    getReferenceFrameMax,
    normalizeRef,
    normalizeStage,
    normalizeStageControlNetStrengthValue,
    normalizeStageLoras,
    normalizeStageRefStrengths,
    normalizeStageRefStrengthValue,
    readRawStageProp,
    readRawStageString,
    removeRefAt,
} from "./normalizationStage";
