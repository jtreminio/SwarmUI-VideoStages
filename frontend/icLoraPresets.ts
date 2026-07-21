import { IC_LORA_AUTO_FOLDER } from "./constants";
import type { IcLoraControlType } from "./types";

// Curated LTX-2.3 IC-LoRA presets for the IC-LoRAs section's Preset dropdown: picking one seeds
// strength / control-type defaults and surfaces a trigger-phrase hint. Guidance only — presets never
// touch the prompt or gate behavior (any installed LoRA works via "Custom"), and the preset id rides
// in the JSON purely so the editor reopens with the same context. The exception is the "[AUTO]"
// LoRA choice, which downloads `weightsUrl` to `LTX-2/IC-LoRA/<id>.safetensors` and resolves the
// entry to that model by convention (see icLoraAutoModelName).

export interface IcLoraPreset {
    /** Stable id used as the Preset dropdown value AND the [AUTO] weights file stem — never rename. */
    id: string;
    displayName: string;
    /** Prompt phrase to prepend by hand; "" when none. */
    triggerPhrase: string;
    /** Recommended LoRA model strength; seeds the strength input when applied. */
    strength: number;
    /** Control signal the drive video should be rendered into. */
    controlType: IcLoraControlType;
    /** Direct safetensors URL for the [AUTO] download (verified against the HF repo). */
    weightsUrl: string;
    note: string;
}

/** Sentinel id for the "Custom" (no preset) choice. */
export const IC_LORA_PRESET_CUSTOM_ID = "custom";

const HF = "https://huggingface.co";

export const IC_LORA_PRESETS: readonly IcLoraPreset[] = [
    {
        id: "union-control",
        displayName: "Union Control",
        triggerPhrase: "",
        strength: 1,
        controlType: "depth",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Union-Control/resolve/main/ltx-2.3-22b-ic-lora-union-control-ref0.5.safetensors`,
        note: "Structural control from depth/canny/normal signals; pick the control type to render.",
    },
    {
        id: "motion-track-control",
        displayName: "Motion Track Control",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control/resolve/main/ltx-2.3-22b-ic-lora-motion-track-control-ref0.5.safetensors`,
        note: "Guide motion with sparse point trajectories; feed a pre-rendered track video.",
    },
    {
        id: "in-outpainting",
        displayName: "In/Outpainting",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-In-Outpainting/resolve/main/ltx-2.3-22b-ic-lora-in-outpainting-0.9.safetensors`,
        note: "Fill or extend a masked clip; feed the masked video directly.",
    },
    {
        id: "ingredients",
        displayName: "Ingredients",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.3-22b-ic-lora-ingredients-0.9.safetensors`,
        note: "Consistent characters/props from a reference sheet; feed the sheet as the drive video.",
    },
    {
        id: "lipdub",
        displayName: "LipDub",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-LipDub/resolve/main/ltx-2.3-22b-ic-lora-lipdub-0.9.safetensors`,
        note: "New lip movements matching target audio; pair with this clip's audio track.",
    },
    {
        id: "hdr",
        displayName: "HDR",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        // The repo also ships an auxiliary hdr-scene-emb file; only the LoRA itself is fetched.
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-HDR/resolve/main/ltx-2.3-22b-ic-lora-hdr-0.9.safetensors`,
        note: "16-bit HDR (LogC3) generation; works with no drive video (LoRA-only).",
    },
    {
        id: "pixel-spatial-upscaler-x2",
        displayName: "Pixel Spatial Upscaler ×2",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x2-0.9.safetensors`,
        note: "Creative 2× upscale; feed the low-res clip directly.",
    },
    {
        id: "pixel-spatial-upscaler-x4",
        displayName: "Pixel Spatial Upscaler ×4",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors`,
        note: "Creative 4× upscale; feed the low-res clip directly.",
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
        id: "colorizer",
        displayName: "Colorizer",
        triggerPhrase: "COLORIZE",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/DoctorDiffusion/LTX-2.3-IC-LoRA-Colorizer/resolve/main/LTX-2.3-22b-IC-LoRA-Colorizer-0.9.safetensors`,
        note: "Community. Colorizes black & white footage; feed the grayscale clip. Confirm trigger in README.",
    },
    {
        id: "restyle",
        displayName: "ReStyle",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Cseti/LTX2.3-22B_ReStyle_IC-LoRA/resolve/main/852654_LTX2.3-22B_ReStyle_IC-LoRA_8000_v0.1.safetensors`,
        note: "Community. Style transfer over an existing clip; see README for style prompts.",
    },
    {
        id: "cameraman",
        displayName: "Cameraman",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Cseti/LTX2.3-22B_IC-LoRA-Cameraman_v2/resolve/main/LTX2.3-22B_IC-LoRA-Cameraman_v2_14000.safetensors`,
        note: "Community. Camera-motion control driven by the reference video's movement.",
    },
    {
        id: "crossview-prompt",
        displayName: "CrossView Prompt",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/Cseti/LTX2.3-22B_IC-LoRA-CrossView-Prompt/resolve/main/LTX2.3-22B_IC-LoRA-CrossView-Prompt_v0.9_13700.safetensors`,
        note: "Community. Re-renders the scene from a prompted new camera viewpoint.",
    },
    {
        id: "outpaint",
        displayName: "Outpaint",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/oumoumad/LTX-2.3-22b-IC-LoRA-Outpaint/resolve/main/ltx-2.3-22b-ic-lora-outpaint.safetensors`,
        note: "Community. Extends the frame beyond the source video's borders.",
    },
    {
        id: "refocus",
        displayName: "ReFocus",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/oumoumad/LTX-2.3-22b-IC-LoRA-ReFocus/resolve/main/ltx-2.3-22b-ic-lora-refocus.safetensors`,
        note: "Community. Fixes lens blur / refocuses; feed the blurred clip directly.",
    },
    {
        id: "vr360-outpaint",
        displayName: "VR 360 Outpaint",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        weightsUrl: `${HF}/TheBurgstall/VR-360-Outpaint-LTX2.3-IC-LoRA/resolve/main/360vroutpaint_v2_step09000.safetensors`,
        note: "Community. Outpaints to an equirectangular 360° panorama.",
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

/**
 * The model name an [AUTO] entry resolves to — where the preset's weights land in the LoRA
 * folder. Mirrored by the backend's Constants.IcLoraAutoModelFolder + preset id convention.
 */
export const icLoraAutoModelName = (preset: IcLoraPreset): string =>
    `${IC_LORA_AUTO_FOLDER}/${preset.id}`;

export const icLoraTriggerHint = (preset: IcLoraPreset | null): string => {
    if (!preset?.triggerPhrase) {
        return "";
    }
    return `Prepend "${preset.triggerPhrase}" to your prompt`;
};
