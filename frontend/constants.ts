export const REF_FRAME_MIN = 1;
export const DEFAULT_CLIP_DURATION_SECONDS = 5;
export const CLIP_DURATION_MIN = 1;
export const CLIP_DURATION_MAX = 9999;
export const PROMPT_WINDOW_MIN_DURATION = 0.25;
export const PROMPT_WINDOW_DEFAULT_DURATION = 1.5;
export const RETAKE_MIN_DURATION = 0.1;
export const RETAKE_DEFAULT_DURATION = 2;
export const RETAKE_DURATION_STEP = 0.1;
export const RETAKE_STRENGTH_MIN = 0;
export const RETAKE_STRENGTH_MAX = 1;
export const RETAKE_STRENGTH_STEP = 0.05;
export const RETAKE_STRENGTH_DEFAULT = 1;
export const AUDIO_SEGMENT_MIN_LENGTH = 0.1;
export const AUDIO_SEGMENT_DEFAULT_LENGTH = 2;
export const AUDIO_SEGMENT_STEP = 0.1;
export const ROOT_DIMENSION_MIN = 256;
export const ROOT_DIMENSION_MAX = 4096;
export const ROOT_DIMENSION_STEP = 32;
export const ROOT_FPS_MIN = 1;
export const ROOT_FPS_MAX = 120;
export const CONTROLNET_SOURCE_OPTIONS = [
    "ControlNet 1",
    "ControlNet 2",
    "ControlNet 3",
];
export const STAGE_REF_STRENGTH_MIN = 0;
export const STAGE_REF_STRENGTH_MAX = 1;
export const STAGE_REF_STRENGTH_STEP = 0.1;
export const STAGE_REF_STRENGTH_DEFAULT = 0.8;
export const IMAGE_TO_VIDEO_DEFAULT_REF_STRENGTH = 1;
export const STAGE_CONTROLNET_STRENGTH_MIN = 0;
export const STAGE_CONTROLNET_STRENGTH_MAX = 1;
export const STAGE_CONTROLNET_STRENGTH_STEP = 0.1;
export const STAGE_CONTROLNET_STRENGTH_DEFAULT = 0.8;
// Per-entry IC-LoRA knobs: strength is the LoRA model strength (loader), attention is the
// per-guide self-attention influence (below 1 selects the backend's Advanced guide node).
export const IC_LORA_SOURCE_UPLOAD = "Upload";
export const IC_LORA_SOURCE_STAGE_INPUT = "Stage Input";
export const IC_LORA_STAGE_ALL = -1;
export const IC_LORA_STRENGTH_MIN = 0;
export const IC_LORA_STRENGTH_MAX = 2;
export const IC_LORA_STRENGTH_STEP = 0.05;
export const IC_LORA_STRENGTH_DEFAULT = 1;
export const IC_LORA_ATTENTION_MIN = 0;
export const IC_LORA_ATTENTION_MAX = 1;
export const IC_LORA_ATTENTION_STEP = 0.05;
export const IC_LORA_ATTENTION_DEFAULT = 1;
// "[AUTO]" as an entry's lora value means "use the selected preset's weights, downloading them
// on demand into <lora dir>/LTX-2/IC-LoRA/<original upstream filename>". The folder + filename
// convention is shared with the backend (Constants.IcLoraAutoModel* + IcLoraWeights), which
// resolves "[AUTO]" to the same path at generation time — keep the two in sync.
export const IC_LORA_AUTO = "[AUTO]";
export const IC_LORA_AUTO_FOLDER = "LTX-2/IC-LoRA";

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
    const prefix =
        typeof getImageOutPrefix === "function" ? getImageOutPrefix() : "";
    return `${prefix}/${value}`;
};
