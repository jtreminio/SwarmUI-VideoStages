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

export const LIPDUB_DRIVE_MEDIA_CONTRACT: IcLoraDriveMediaContract = {
    acceptedKinds: ["audio", "video"],
    driveData: "audio",
};

export const icLoraDriveMediaContractForData = (
    driveData: IcLoraDriveData,
): IcLoraDriveMediaContract => {
    if (driveData === "audio") {
        return LIPDUB_DRIVE_MEDIA_CONTRACT;
    }
    if (driveData === "visual") {
        return DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;
    }
    return { acceptedKinds: [], driveData: "none" };
};

export const IC_LORA_PRESET_CUSTOM_ID = "custom";
export const IC_LORA_DEFAULT_PRESET_ID = "union-control";

type IcLoraPresetId = (typeof IC_LORA_WEIGHTS)[number]["id"];

/** Everything about a preset that has no backend owner. */
type IcLoraPresetPresentation = Omit<
    IcLoraPreset,
    "id" | "weightsUrl" | "dimensionDownscaleFactor"
>;

const IC_LORA_PRESENTATION: Record<IcLoraPresetId, IcLoraPresetPresentation> = {
    "union-control": {
        displayName: "Union Control",
        triggerPhrase: "",
        strength: 1,
        controlType: "depth",
        allowedControlTypes: ["none", "canny", "depth", "normal"],
        note: "Structural control from depth/canny/normal signals; pick the control type to render. Dims snap to multiples of 64.",
    },
    "motion-track-control": {
        displayName: "Motion Track Control",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Feed an LTXVDrawTracks-rendered track video (e.g. saved from the official workflow) — hand-made dot videos don't match the training format. Dims snap to multiples of 64.",
    },
    "in-outpainting": {
        displayName: "In/Outpainting",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Feed a pre-masked clip: masked region must be hard #66FF00 green, slightly dilated, losslessly encoded. Kept regions are still re-generated, not composited back.",
    },
    ingredients: {
        displayName: "Ingredients",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Feed the reference sheet as drive media (a still image works). Prompt pattern: '### Reference Sheet Description' per cell, then '### Target Description'.",
    },
    lipdub: {
        displayName: "LipDub",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Generates new speech + lips from the prompt's words. The drive source supplies the speaker sample: audio is used directly, and video sources contribute only their audio while their frames are ignored.",
        driveMedia: LIPDUB_DRIVE_MEDIA_CONTRACT,
    },
    "pixel-spatial-upscaler-x2": {
        displayName: "Pixel Spatial Upscaler ×2",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Apply on a refine stage with Upscale ×2 and source Incoming media. Dims snap to multiples of 64.",
    },
    "pixel-spatial-upscaler-x4": {
        displayName: "Pixel Spatial Upscaler ×4",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Apply on a refine stage with Upscale ×4 and source Incoming media. Dims snap to multiples of 128.",
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
        strength: 1.2,
        controlType: "none",
        note: "Sweet spot ~1.2 (1.0 subtle; ≥1.5 warps faces). Feed a dry clip.",
    },
    "instant-shave": {
        displayName: "Instant Shave",
        triggerPhrase: "REMOVEBEARD",
        strength: 1,
        controlType: "none",
        note: "Feed a bearded clip directly. Lower toward 0.8 if artifacts appear.",
    },
    colorization: {
        displayName: "Colorization",
        triggerPhrase: "COLORIZE",
        strength: 1,
        controlType: "none",
        note: "Feed the grayscale clip; describe the restored colors after the COLORIZE trigger.",
    },
    "cross-eyed": {
        displayName: "Cross-Eyed",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Turns straight eyes inward (convergent strabismus) in close-up portrait clips; describe the effect in the prompt.",
    },
    "day-to-night": {
        displayName: "Day to Night",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Relights a daytime clip to night. Prompt the night look and add 'Only the lighting changes from day to night'. Best at ~4s clips.",
    },
    restyle: {
        displayName: "ReStyle",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Style transfer over an existing clip; see README for style prompts.",
    },
    cameraman: {
        displayName: "Cameraman",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Camera-motion control driven by the reference video's movement.",
    },
    "crossview-prompt": {
        displayName: "CrossView Prompt",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Re-renders the scene from a prompted new camera viewpoint.",
    },
    outpaint: {
        displayName: "Outpaint",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Extends the frame beyond the source video's borders.",
    },
    refocus: {
        displayName: "ReFocus",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Fixes lens blur / refocuses; feed the blurred clip directly.",
    },
    "vr360-outpaint": {
        displayName: "VR 360 Outpaint",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Outpaints to an equirectangular 360° panorama.",
    },
};

export const IC_LORA_PRESETS: readonly IcLoraPreset[] = IC_LORA_WEIGHTS.map(
    (weights) => ({ ...IC_LORA_PRESENTATION[weights.id], ...weights }),
);

/** Returns null for "Custom", unknown ids, or empty input. */
export const findIcLoraPreset = (id: string): IcLoraPreset | null => {
    const wanted = `${id ?? ""}`.trim();
    if (!wanted || wanted === IC_LORA_PRESET_CUSTOM_ID) {
        return null;
    }
    return IC_LORA_PRESETS.find((preset) => preset.id === wanted) ?? null;
};

/** Human-facing guide label: preset display name, or the selected custom model. */
export const icLoraDisplayName = (
    entry: Pick<IcLora, "preset" | "lora">,
): string => {
    if (entry.preset === IC_LORA_PRESET_CUSTOM_ID) {
        return entry.lora;
    }
    return findIcLoraPreset(entry.preset)?.displayName ?? entry.preset;
};

/** Returns the LTX media contract for a preset, including Custom/unknown visual defaults. */
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

/** The dotted name auto-downloads used before core owned the transfer; still accepted, never written. */
export const icLoraLegacyAutoModelName = (preset: IcLoraPreset): string =>
    `${IC_LORA_AUTO_FOLDER}/${icLoraWeightsStem(preset)}`;

/** The Hugging Face repo page a preset's weights come from. */
export const icLoraRepoUrl = (preset: IcLoraPreset): string =>
    preset.weightsUrl.split("/resolve/")[0];

export const icLoraTriggerHint = (preset: IcLoraPreset | null): string => {
    if (!preset?.triggerPhrase) {
        return "";
    }
    return `Prepend "${preset.triggerPhrase}" to your prompt`;
};
