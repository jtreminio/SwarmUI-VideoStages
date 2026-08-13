import { clamp } from "./constants";

export const H3_ATTENTION_WINDOW_FEATURE = "h3_window_attention";
export const H3_ATTENTION_WINDOW_MIN_SECONDS = 0;
export const H3_ATTENTION_WINDOW_MAX_SECONDS = 20;
export const H3_ATTENTION_WINDOW_STEP_SECONDS = 0.5;

export const normalizeH3AttentionWindowSeconds = (value: unknown): number => {
    const numeric = Number(value);
    if (!Number.isFinite(numeric)) {
        return H3_ATTENTION_WINDOW_MIN_SECONDS;
    }
    return clamp(
        Math.round(numeric / H3_ATTENTION_WINDOW_STEP_SECONDS) *
            H3_ATTENTION_WINDOW_STEP_SECONDS,
        H3_ATTENTION_WINDOW_MIN_SECONDS,
        H3_ATTENTION_WINDOW_MAX_SECONDS,
    );
};
