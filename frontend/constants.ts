import { getVideoStagesHostBridge } from "./host";

export const REF_FRAME_MIN = 1;
export const DEFAULT_CLIP_DURATION_SECONDS = 5;
export const CLIP_DURATION_MIN = 1;
export const CLIP_DURATION_MAX = 9999;
export const PROMPT_WINDOW_MIN_DURATION = 0.25;
export const PROMPT_WINDOW_DEFAULT_DURATION = 3;
export const RETAKE_MIN_DURATION = 0.1;
export const RETAKE_DEFAULT_DURATION = 3;
export const RETAKE_DURATION_STEP = 0.1;
export const RETAKE_STRENGTH_MIN = 0;
export const RETAKE_STRENGTH_MAX = 1;
export const RETAKE_STRENGTH_STEP = 0.05;
export const RETAKE_STRENGTH_DEFAULT = 1;
export const AUDIO_SEGMENT_MIN_LENGTH = 0.1;
export const AUDIO_SEGMENT_DEFAULT_LENGTH = 2;
export const AUDIO_SEGMENT_STEP = 0.1;
export const AUDIO_SEGMENT_VOLUME_MIN = 0.00001;
export const AUDIO_SEGMENT_VOLUME_MAX = 100000;
export const AUDIO_SEGMENT_VOLUME_SLIDER_MIN = 0.1;
export const AUDIO_SEGMENT_VOLUME_SLIDER_MAX = 4;
export const AUDIO_SEGMENT_VOLUME_SLIDER_STEP = 0.1;
export const AUDIO_SEGMENT_VOLUME_DEFAULT = 1;
export const ROOT_DIMENSION_MIN = 256;
export const ROOT_DIMENSION_MAX = 4096;
export const ROOT_DIMENSION_STEP = 32;
export const ROOT_FPS_MIN = 1;
export const ROOT_FPS_MAX = 120;
export const STAGE_REF_STRENGTH_MIN = 0;
export const STAGE_REF_STRENGTH_MAX = 1;
export const STAGE_REF_STRENGTH_STEP = 0.1;
export const STAGE_REF_STRENGTH_DEFAULT = 0.8;
export const IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH = 1;

export const parseBase2EditStageIndex = (value: string): number | null => {
    const match = `${value || ""}`
        .trim()
        .replace(/\s+/g, "")
        .match(/^edit(\d+)$/i);
    if (!match) {
        return null;
    }
    return parseInt(match[1], 10);
};

export const normalizeUploadFileName = (
    value: string | null | undefined,
): string | null => {
    const raw = `${value ?? ""}`.trim();
    if (!raw) {
        return null;
    }
    const slashIndex = Math.max(raw.lastIndexOf("/"), raw.lastIndexOf("\\"));
    return slashIndex >= 0 ? raw.slice(slashIndex + 1) : raw;
};

export const clamp = (value: number, min: number, max: number): number =>
    Math.min(Math.max(value, min), max);

export const mediaPreviewSrc = (value: string): string => {
    if (`${value ?? ""}`.startsWith("data:")) {
        return value;
    }
    const prefix = getVideoStagesHostBridge().getMediaOutputPrefix();
    return `${prefix}/${value}`;
};
