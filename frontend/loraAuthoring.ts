import type { RootDefaults } from "./types";

export const LORA_WEIGHT_DEFAULT = 1;
export const LORA_WEIGHT_STEP = 0.05;

/** Matches SwarmUI's LoRA picker: model metadata wins, otherwise weight 1. */
export const defaultLoraWeight = (
    defaults: Pick<RootDefaults, "loraValues" | "loraDefaultWeights">,
    modelName: string,
): number => {
    const index = defaults.loraValues.indexOf(modelName);
    const value = index >= 0 ? defaults.loraDefaultWeights[index] : null;
    return typeof value === "number" && Number.isFinite(value)
        ? value
        : LORA_WEIGHT_DEFAULT;
};
