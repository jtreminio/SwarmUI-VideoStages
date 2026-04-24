export const REF_FRAME_MIN = 1;
export const DEFAULT_CLIP_DURATION_SECONDS = 5;
export const CLIP_DURATION_MIN = 1;
export const CLIP_DURATION_MAX = 9999;
export const CLIP_DURATION_SLIDER_MAX = 60;
export const CLIP_DURATION_SLIDER_STEP = 0.5;
export const ROOT_DIMENSION_MIN = 256;
export const ROOT_FPS_MIN = 4;
export const CLIP_AUDIO_UPLOAD_FIELD = "uploadedAudio";
export const CLIP_AUDIO_UPLOAD_LABEL = "Audio Upload";
export const CLIP_AUDIO_UPLOAD_DESCRIPTION =
    "Audio file to attach to this clip. Used when Audio Source is set to Upload.";
export const STAGE_REF_STRENGTH_MIN = 0.1;
export const STAGE_REF_STRENGTH_MAX = 1;
export const STAGE_REF_STRENGTH_STEP = 0.1;
export const STAGE_REF_STRENGTH_DEFAULT = 0.8;
export const IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH = 1;
export const STAGE_REF_STRENGTH_FIELD_PREFIX = "refStrength_";

export interface CachedRefUpload {
    src: string;
    name: string;
}

export const stageRefStrengthField = (refIdx: number): string =>
    `${STAGE_REF_STRENGTH_FIELD_PREFIX}${refIdx}`;

export const parseStageRefStrengthIndex = (field: string): number | null => {
    if (!field.startsWith(STAGE_REF_STRENGTH_FIELD_PREFIX)) {
        return null;
    }
    const refIdx = parseInt(
        field.slice(STAGE_REF_STRENGTH_FIELD_PREFIX.length),
        10,
    );
    if (!Number.isInteger(refIdx) || refIdx < 0) {
        return null;
    }
    return refIdx;
};

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

export const refUploadKey = (clipIdx: number, refIdx: number): string =>
    `${clipIdx}:${refIdx}`;

export const parseRefUploadKey = (
    key: string,
): { clipIdx: number; refIdx: number } | null => {
    const parts = key.split(":");
    if (parts.length !== 2) {
        return null;
    }
    const clipIdx = parseInt(parts[0], 10);
    const refIdx = parseInt(parts[1], 10);
    if (!Number.isInteger(clipIdx) || !Number.isInteger(refIdx)) {
        return null;
    }
    return { clipIdx, refIdx };
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
