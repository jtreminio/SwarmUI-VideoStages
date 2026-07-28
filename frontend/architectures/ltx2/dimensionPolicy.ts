import { ROOT_DIMENSION_STEP } from "../../constants";
import type { Clip, IcLora } from "../../types";
import {
    findIcLoraPreset,
    icLoraAutoModelName,
    icLoraLegacyAutoModelName,
} from "./icLoraPresets";

const presetFactors = new Map<string, number>([
    ["union-control", 2],
    ["motion-track-control", 2],
    ["pixel-spatial-upscaler-x2", 2],
    ["pixel-spatial-upscaler-x4", 4],
]);

const normalizeModelName = (value: string): string => {
    const normalized = `${value ?? ""}`.trim().replaceAll("\\", "/");
    const basename = normalized.slice(normalized.lastIndexOf("/") + 1);
    return basename.replace(/\.safetensors$/i, "").toLowerCase();
};

const curatedModelFactors = new Map<string, number>();
for (const [presetId, factor] of presetFactors) {
    const preset = findIcLoraPreset(presetId);
    for (const name of preset
        ? [icLoraAutoModelName(preset), icLoraLegacyAutoModelName(preset)]
        : []) {
        curatedModelFactors.set(normalizeModelName(name), factor);
    }
}

export const icLoraDimensionFactor = (
    entry: Pick<IcLora, "preset" | "lora">,
): number => {
    const presetFactor = presetFactors.get(
        `${entry.preset ?? ""}`.trim().toLowerCase(),
    );
    if (presetFactor) {
        return presetFactor;
    }
    return curatedModelFactors.get(normalizeModelName(entry.lora)) ?? 1;
};

export const ltx2DimensionFactor = (clip: Pick<Clip, "icLoras">): number =>
    Math.max(1, ...clip.icLoras.map((entry) => icLoraDimensionFactor(entry)));

/** LTX adds its active IC-LoRA reference grid to the global /32 document grid. */
export const ltx2DimensionMultiple = (clip: Pick<Clip, "icLoras">): number =>
    ROOT_DIMENSION_STEP * ltx2DimensionFactor(clip);
