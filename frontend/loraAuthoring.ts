import type { Clip, RootDefaults } from "./types";

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

export const appendLoraToClip = (
    clip: Clip,
    name: string,
    initialWeight: number,
): void => {
    clip.loras.push({ name, weight: initialWeight });
};

export const replaceLoraModelAt = (
    clip: Clip,
    index: number,
    name: string,
    initialWeight: number,
): boolean => {
    const entry = clip.loras[index];
    if (!entry) {
        return false;
    }
    entry.name = name;
    entry.weight = initialWeight;
    return true;
};

export const removeLoraAt = (clip: Clip, index: number): boolean => {
    if (index < 0 || index >= clip.loras.length) {
        return false;
    }
    clip.loras.splice(index, 1);
    return true;
};
