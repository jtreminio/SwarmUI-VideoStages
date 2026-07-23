import type { IcLoraControlType } from "../../types";

const IC_LORA_AUTO_FOLDER = "LTX-2/IC-LoRA";

// Curated LTX-2.3 IC-LoRA presets for the IC-LoRAs section's Preset dropdown: picking one seeds
// strength / control-type defaults and surfaces a trigger-phrase hint. Most presets are guidance only;
// the media contract is an LTX runtime behavior declaration (any installed LoRA still works via
// "Custom"). The exception is the "[AUTO]"
// LoRA choice, which downloads the weights to `LTX-2/IC-LoRA/<original upstream filename>` and
// resolves the entry to that model by convention (see icLoraAutoModelName).

export interface IcLoraPreset {
    /** Stable id used as the Preset dropdown value AND the backend IcLoraWeights key — never rename. */
    id: string;
    displayName: string;
    /** Prompt phrase to prepend by hand; "" when none. */
    triggerPhrase: string;
    /** Seeds the strength input when applied. */
    strength: number;
    /** Control signal the drive video should be rendered into. */
    controlType: IcLoraControlType;
    /** Direct safetensors URL for the [AUTO] download. */
    weightsUrl: string;
    note: string;
    /** LTX-owned declaration of which part of one Drive Media upload this preset consumes. */
    driveMedia?: IcLoraDriveMediaContract;
}

export type IcLoraDriveMediaKind = "image" | "video" | "audio";
export type IcLoraDriveMediaConsumption = "visual" | "audio";

export interface IcLoraDriveMediaContract {
    acceptedKinds: readonly IcLoraDriveMediaKind[];
    consumes: IcLoraDriveMediaConsumption;
    visualSource: "drive-media" | "clip-entry";
    requiresUpload: boolean;
}

export const DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT: IcLoraDriveMediaContract = {
    acceptedKinds: ["image", "video"],
    consumes: "visual",
    visualSource: "drive-media",
    requiresUpload: false,
};

export const LIPDUB_DRIVE_MEDIA_CONTRACT: IcLoraDriveMediaContract = {
    acceptedKinds: ["audio", "video"],
    consumes: "audio",
    visualSource: "clip-entry",
    requiresUpload: true,
};

/** Sentinel id for the "Custom" (no preset) choice. */
export const IC_LORA_PRESET_CUSTOM_ID = "custom";

/** The one curated preset whose LoRA consumes rendered control signals (canny/depth/normal). */
export const IC_LORA_PRESET_UNION_CONTROL_ID = "union-control";

const HF = "https://huggingface.co";

export const IC_LORA_PRESETS: readonly IcLoraPreset[] = [
    {
        id: "union-control",
        displayName: "Union Control",
        triggerPhrase: "",
        strength: 1,
        controlType: "depth",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Union-Control/resolve/main/ltx-2.3-22b-ic-lora-union-control-ref0.5.safetensors`,
        note: "Structural control from depth/canny/normal signals; pick the control type to render. Dims snap to multiples of 64.",
    },
    {
        id: "motion-track-control",
        displayName: "Motion Track Control",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control/resolve/main/ltx-2.3-22b-ic-lora-motion-track-control-ref0.5.safetensors`,
        note: "Feed an LTXVDrawTracks-rendered track video (e.g. saved from the official workflow) — hand-made dot videos don't match the training format. Dims snap to multiples of 64.",
    },
    {
        id: "in-outpainting",
        displayName: "In/Outpainting",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-In-Outpainting/resolve/main/ltx-2.3-22b-ic-lora-in-outpainting-0.9.safetensors`,
        note: "Feed a pre-masked clip: masked region must be hard #66FF00 green, slightly dilated, losslessly encoded. Kept regions are still re-generated, not composited back.",
    },
    {
        id: "ingredients",
        displayName: "Ingredients",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.3-22b-ic-lora-ingredients-0.9.safetensors`,
        note: "Feed the reference sheet as drive media (a still image works). Prompt pattern: '### Reference Sheet Description' per cell, then '### Target Description'.",
    },
    {
        id: "lipdub",
        displayName: "LipDub",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-LipDub/resolve/main/ltx-2.3-22b-ic-lora-lipdub-0.9.safetensors`,
        note: "Generates new speech + lips from the prompt's words. Drive Media supplies the speaker sample: audio is used directly, and video uploads have their audio extracted while their frames are ignored.",
        driveMedia: LIPDUB_DRIVE_MEDIA_CONTRACT,
    },
    {
        id: "hdr",
        displayName: "HDR",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        // The repo also ships an auxiliary hdr-scene-emb file; only the LoRA itself is fetched.
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-HDR/resolve/main/ltx-2.3-22b-ic-lora-hdr-0.9.safetensors`,
        note: "HDR generation; feed the SDR clip as the drive video. Output is auto-tonemapped to SDR (LogC3 decompressed). Suggested prompt: 'HDR footage'.",
    },
    {
        id: "pixel-spatial-upscaler-x2",
        displayName: "Pixel Spatial Upscaler ×2",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x2-0.9.safetensors`,
        note: "Apply on a refine stage with Upscale ×2 and source Stage Input. Dims snap to multiples of 64.",
    },
    {
        id: "pixel-spatial-upscaler-x4",
        displayName: "Pixel Spatial Upscaler ×4",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors`,
        note: "Apply on a refine stage with Upscale ×4 and source Stage Input. Dims snap to multiples of 128.",
    },
    {
        id: "deblur",
        displayName: "Deblur",
        triggerPhrase: "DEBLUR",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Deblur/resolve/main/ltx-2.3-22b-ic-lora-deblur-0.9.safetensors`,
        note: "Feed the blurry clip directly. Lower toward 0.8 if over-sharpened.",
    },
    {
        id: "decompression",
        displayName: "Decompression",
        triggerPhrase: "ENHANCE QUALITY",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Decompression/resolve/main/ltx-2.3-22b-ic-lora-decompression-0.9.safetensors`,
        note: "Removes compression artifacts; feed a low-bitrate clip directly.",
    },
    {
        id: "water-simulation",
        displayName: "Water Simulation",
        triggerPhrase: "ADD WATER",
        strength: 1.2,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Water-Simulation/resolve/main/ltx-2.3-22b-ic-lora-water-simulation-0.9.safetensors`,
        note: "Sweet spot ~1.2 (1.0 subtle; ≥1.5 warps faces). Feed a dry clip.",
    },
    {
        id: "instant-shave",
        displayName: "Instant Shave",
        triggerPhrase: "REMOVEBEARD",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Instant-Shave/resolve/main/ltx-2.3-22b-ic-lora-instant-shave-0.9.safetensors`,
        note: "Feed a bearded clip directly. Lower toward 0.8 if artifacts appear.",
    },
    {
        id: "colorization",
        displayName: "Colorization",
        triggerPhrase: "COLORIZE",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Colorization/resolve/main/ltx-2.3-22b-ic-lora-colorization-0.9.safetensors`,
        note: "Feed the grayscale clip; describe the restored colors after the COLORIZE trigger.",
    },
    {
        id: "cross-eyed",
        displayName: "Cross-Eyed",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Cross-Eyed/resolve/main/ltx-2.3-22b-ic-lora-cross-eyed-0.9.safetensors`,
        note: "Turns straight eyes inward (convergent strabismus) in close-up portrait clips; describe the effect in the prompt.",
    },
    {
        id: "day-to-night",
        displayName: "Day to Night",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Day-To-Night/resolve/main/ltx-2.3-22b-ic-lora-day-to-night-0.9.safetensors`,
        note: "Relights a daytime clip to night. Prompt the night look and add 'Only the lighting changes from day to night'. Best at ~4s clips.",
    },
    {
        id: "restyle",
        displayName: "ReStyle",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Cseti/LTX2.3-22B_ReStyle_IC-LoRA/resolve/main/852654_LTX2.3-22B_ReStyle_IC-LoRA_8000_v0.1.safetensors`,
        note: "Style transfer over an existing clip; see README for style prompts.",
    },
    {
        id: "cameraman",
        displayName: "Cameraman",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Cseti/LTX2.3-22B_IC-LoRA-Cameraman_v2/resolve/main/LTX2.3-22B_IC-LoRA-Cameraman_v2_14000.safetensors`,
        note: "Camera-motion control driven by the reference video's movement.",
    },
    {
        id: "crossview-prompt",
        displayName: "CrossView Prompt",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Cseti/LTX2.3-22B_IC-LoRA-CrossView-Prompt/resolve/main/LTX2.3-22B_IC-LoRA-CrossView-Prompt_v0.9_13700.safetensors`,
        note: "Re-renders the scene from a prompted new camera viewpoint.",
    },
    {
        id: "outpaint",
        displayName: "Outpaint",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/oumoumad/LTX-2.3-22b-IC-LoRA-Outpaint/resolve/main/ltx-2.3-22b-ic-lora-outpaint.safetensors`,
        note: "Extends the frame beyond the source video's borders.",
    },
    {
        id: "refocus",
        displayName: "ReFocus",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/oumoumad/LTX-2.3-22b-IC-LoRA-ReFocus/resolve/main/ltx-2.3-22b-ic-lora-refocus.safetensors`,
        note: "Fixes lens blur / refocuses; feed the blurred clip directly.",
    },
    {
        id: "vr360-outpaint",
        displayName: "VR 360 Outpaint",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/TheBurgstall/VR-360-Outpaint-LTX2.3-IC-LoRA/resolve/main/360vroutpaint_v2_step09000.safetensors`,
        note: "Outpaints to an equirectangular 360° panorama.",
    },
];

/** Returns null for "Custom", unknown ids, or empty input. */
export const findIcLoraPreset = (id: string): IcLoraPreset | null => {
    const wanted = `${id ?? ""}`.trim();
    if (!wanted || wanted === IC_LORA_PRESET_CUSTOM_ID) {
        return null;
    }
    return IC_LORA_PRESETS.find((preset) => preset.id === wanted) ?? null;
};

/** Returns the LTX media contract for a preset, including Custom/unknown visual defaults. */
export const icLoraDriveMediaContract = (
    preset: IcLoraPreset | null,
): IcLoraDriveMediaContract =>
    preset?.driveMedia ?? DEFAULT_IC_LORA_DRIVE_MEDIA_CONTRACT;

export const icLoraPresetConsumesAudio = (presetId: string): boolean =>
    icLoraDriveMediaContract(findIcLoraPreset(presetId)).consumes === "audio";

/**
 * The model name an [AUTO] entry resolves to — where the preset's weights land in the LoRA
 * folder, keeping the upstream filename. Mirrored by the backend's IcLoraWeights, which
 * derives the same name from the same URL.
 */
export const icLoraAutoModelName = (preset: IcLoraPreset): string => {
    const file = preset.weightsUrl.slice(
        preset.weightsUrl.lastIndexOf("/") + 1,
    );
    return `${IC_LORA_AUTO_FOLDER}/${file.replace(/\.safetensors$/i, "")}`;
};

/** The Hugging Face repo page a preset's weights come from. */
export const icLoraRepoUrl = (preset: IcLoraPreset): string =>
    preset.weightsUrl.split("/resolve/")[0];

export const icLoraTriggerHint = (preset: IcLoraPreset | null): string => {
    if (!preset?.triggerPhrase) {
        return "";
    }
    return `Prepend "${preset.triggerPhrase}" to your prompt`;
};
