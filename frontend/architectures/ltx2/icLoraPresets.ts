import type {
    IcLora,
    IcLoraControlType,
    IcLoraDriveData,
    IcLoraDriveMediaKind,
} from "../../types";

import { IC_LORA_AUTO_FOLDER, IC_LORA_WEIGHTS } from "./generatedIcLora";

export { IC_LORA_AUTO } from "./generatedIcLora";

export interface IcLoraPreset {
    id: string;
    displayName: string;
    triggerPhrase: string;
    strength: number;
    controlType: IcLoraControlType;
    allowedControlTypes?: readonly IcLoraControlType[];
    weightsUrl: string;
    dimensionDownscaleFactor: number;
    note: string;
    driveMedia?: IcLoraDriveMediaContract;
}

export interface IcLoraDriveMediaContract {
    acceptedKinds: readonly IcLoraDriveMediaKind[];
    driveData: IcLoraDriveData;
}

export const DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT: IcLoraDriveMediaContract = {
    acceptedKinds: ["image", "video"],
    driveData: "visual",
};

export const AUDIO_DRIVE_MEDIA_CONTRACT: IcLoraDriveMediaContract = {
    acceptedKinds: ["audio", "video"],
    driveData: "audio",
};

export const icLoraDriveMediaContractForData = (
    driveData: IcLoraDriveData,
): IcLoraDriveMediaContract => {
    if (driveData === "audio") {
        return AUDIO_DRIVE_MEDIA_CONTRACT;
    }
    if (driveData === "visual") {
        return DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;
    }
    return { acceptedKinds: [], driveData: "none" };
};

export const IC_LORA_PRESET_CUSTOM_ID = "custom";
export const IC_LORA_DEFAULT_PRESET_ID = "pixel-spatial-upscaler-2.5-x2";

type IcLoraPresetId = (typeof IC_LORA_WEIGHTS)[number]["id"];

type IcLoraPresetPresentation = Omit<
    IcLoraPreset,
    "id" | "weightsUrl" | "dimensionDownscaleFactor"
>;

const IC_LORA_PRESENTATION: Record<IcLoraPresetId, IcLoraPresetPresentation> = {
    ingredients: {
        displayName: "Ingredients",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Feed the reference sheet as drive media (a still image works). Prompt pattern: '### Reference Sheet Description' per cell, then '### Target Description'.",
    },
    detailer: {
        displayName: "Detailer",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Enhances fine details and textures. Apply to a low-control refine stage. Trained on LTX-2 19B.",
        driveMedia: icLoraDriveMediaContractForData("none"),
    },
    "pixel-spatial-upscaler-2.5-x2": {
        displayName: "Pixel Spatial Upscaler 2.5 ×2",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Apply on a refine stage with Upscale ×2 and source Incoming media. Dims snap to multiples of 64.",
    },
    deblur: {
        displayName: "Deblur",
        triggerPhrase: "DEBLUR",
        strength: 1,
        controlType: "none",
        note: "Feed the blurry clip directly. Lower toward 0.8 if over-sharpened.",
    },
    decompression: {
        displayName: "Decompression",
        triggerPhrase: "ENHANCE QUALITY",
        strength: 1,
        controlType: "none",
        note: "Removes compression artifacts; feed a low-bitrate clip directly.",
    },
    "water-simulation": {
        displayName: "Water Simulation",
        triggerPhrase: "ADD WATER",
        strength: 1,
        controlType: "none",
        note: "Feed a dry clip. The weights are pre-scaled for strength 1.0.",
    },
    colorization: {
        displayName: "Colorization",
        triggerPhrase: "COLORIZE",
        strength: 1,
        controlType: "none",
        note: "Feed the grayscale clip; describe the restored colors after the COLORIZE trigger.",
    },
    "day-to-night": {
        displayName: "Day to Night",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Relights a daytime clip to night. Prompt the night look and add 'Only the lighting changes from day to night'. Best at ~4s clips.",
    },
};

export const IC_LORA_PRESETS: readonly IcLoraPreset[] = IC_LORA_WEIGHTS.map(
    (weights) => ({ ...IC_LORA_PRESENTATION[weights.id], ...weights }),
);

export const findIcLoraPreset = (id: string): IcLoraPreset | null => {
    const wanted = `${id ?? ""}`.trim();
    if (!wanted || wanted === IC_LORA_PRESET_CUSTOM_ID) {
        return null;
    }
    return IC_LORA_PRESETS.find((preset) => preset.id === wanted) ?? null;
};

export const icLoraDisplayName = (
    entry: Pick<IcLora, "preset" | "lora">,
): string => {
    if (entry.preset === IC_LORA_PRESET_CUSTOM_ID) {
        return entry.lora;
    }
    return findIcLoraPreset(entry.preset)?.displayName ?? entry.preset;
};

export const icLoraDriveMediaContract = (
    preset: IcLoraPreset | null,
): IcLoraDriveMediaContract =>
    preset?.driveMedia ?? DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;

const icLoraWeightsStem = (preset: IcLoraPreset): string =>
    preset.weightsUrl
        .slice(preset.weightsUrl.lastIndexOf("/") + 1)
        .replace(/\.safetensors$/i, "");

/** [AUTO] model name; core's downloader strips dots from the requested name. */
export const icLoraAutoModelName = (preset: IcLoraPreset): string =>
    `${IC_LORA_AUTO_FOLDER}/${icLoraWeightsStem(preset).replaceAll(".", "_")}`;

// Accept dotted names from older downloads.
export const icLoraLegacyAutoModelName = (preset: IcLoraPreset): string =>
    `${IC_LORA_AUTO_FOLDER}/${icLoraWeightsStem(preset)}`;

export const icLoraRepoUrl = (preset: IcLoraPreset): string =>
    preset.weightsUrl.split("/resolve/")[0];

export const icLoraTriggerHint = (preset: IcLoraPreset | null): string => {
    if (!preset?.triggerPhrase) {
        return "";
    }
    return `Prepend "${preset.triggerPhrase}" to your prompt`;
};
