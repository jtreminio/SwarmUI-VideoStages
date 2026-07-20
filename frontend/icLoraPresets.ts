import type { IcLoraControlType } from "./types";

// Curated LTX-2.3 IC-LoRA presets for the IC-LoRAs section's Preset dropdown: picking one seeds
// strength / control-type defaults and surfaces a trigger-phrase hint. Guidance only — presets never
// touch the prompt or gate behavior (any installed LoRA works via "Custom"), and the preset id rides
// in the JSON purely so the editor reopens with the same context.

export interface IcLoraPreset {
    /** Stable id used as the Preset dropdown value. */
    id: string;
    displayName: string;
    /** Prompt phrase to prepend by hand; "" when none. */
    triggerPhrase: string;
    /** Recommended LoRA model strength; seeds the strength input when applied. */
    strength: number;
    /** Control signal the drive video should be rendered into. */
    controlType: IcLoraControlType;
    note: string;
}

/** Sentinel id for the "Custom" (no preset) choice. */
export const IC_LORA_PRESET_CUSTOM_ID = "custom";

export const IC_LORA_PRESETS: readonly IcLoraPreset[] = [
    {
        id: "union-control",
        displayName: "Union Control",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Union-Control
        triggerPhrase: "",
        strength: 1,
        controlType: "depth",
        note: "Structural control from depth/canny/normal signals; pick the control type to render.",
    },
    {
        id: "motion-track-control",
        displayName: "Motion Track Control",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Guide motion with sparse point trajectories; feed a pre-rendered track video.",
    },
    {
        id: "in-outpainting",
        displayName: "In/Outpainting",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-In-Outpainting
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Fill or extend a masked clip; feed the masked video directly.",
    },
    {
        id: "ingredients",
        displayName: "Ingredients",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Ingredients
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Consistent characters/props from a reference sheet; feed the sheet as the drive video.",
    },
    {
        id: "lipdub",
        displayName: "LipDub",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-LipDub
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "New lip movements matching target audio; pair with this clip's audio track.",
    },
    {
        id: "hdr",
        displayName: "HDR",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-HDR
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "16-bit HDR (LogC3) generation; works with no drive video (LoRA-only).",
    },
    {
        id: "pixel-spatial-upscaler",
        displayName: "Pixel Spatial Upscaler",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Creative 2×/4× upscale; feed the low-res clip directly.",
    },
    {
        id: "deblur",
        displayName: "Deblur",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Deblur
        triggerPhrase: "DEBLUR",
        strength: 1,
        controlType: "none",
        note: "Feed the blurry clip directly. Lower toward 0.8 if over-sharpened.",
    },
    {
        id: "decompression",
        displayName: "Decompression",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Decompression
        triggerPhrase: "ENHANCE QUALITY",
        strength: 1,
        controlType: "none",
        note: "Removes compression artifacts; feed a low-bitrate clip directly.",
    },
    {
        id: "water-simulation",
        displayName: "Water Simulation",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Water-Simulation
        triggerPhrase: "ADD WATER",
        strength: 1.2,
        controlType: "none",
        note: "Sweet spot ~1.2 (1.0 subtle; ≥1.5 warps faces). Feed a dry clip.",
    },
    {
        id: "instant-shave",
        displayName: "Instant Shave",
        // weights: Lightricks/LTX-2.3-22b-IC-LoRA-Instant-Shave
        triggerPhrase: "REMOVEBEARD",
        strength: 1,
        controlType: "none",
        note: "Feed a bearded clip directly. Lower toward 0.8 if artifacts appear.",
    },
    {
        id: "colorizer",
        displayName: "Colorizer",
        // weights: DoctorDiffusion/LTX-2.3-IC-LoRA-Colorizer
        triggerPhrase: "COLORIZE",
        strength: 1,
        controlType: "none",
        note: "Community. Colorizes black & white footage; feed the grayscale clip. Confirm trigger in README.",
    },
    {
        id: "restyle",
        displayName: "ReStyle",
        // weights: Cseti/LTX2.3-22B_ReStyle_IC-LoRA
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Style transfer over an existing clip; see README for style prompts.",
    },
    {
        id: "cameraman",
        displayName: "Cameraman",
        // weights: Cseti/LTX2.3-22B_IC-LoRA-Cameraman_v2
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Camera-motion control driven by the reference video's movement.",
    },
    {
        id: "crossview-prompt",
        displayName: "CrossView Prompt",
        // weights: Cseti/LTX2.3-22B_IC-LoRA-CrossView-Prompt
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Re-renders the scene from a prompted new camera viewpoint.",
    },
    {
        id: "outpaint",
        displayName: "Outpaint",
        // weights: oumoumad/LTX-2.3-22b-IC-LoRA-Outpaint
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Extends the frame beyond the source video's borders.",
    },
    {
        id: "refocus",
        displayName: "ReFocus",
        // weights: oumoumad/LTX-2.3-22b-IC-LoRA-ReFocus
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Fixes lens blur / refocuses; feed the blurred clip directly.",
    },
    {
        id: "vr360-outpaint",
        displayName: "VR 360 Outpaint",
        // weights: TheBurgstall/VR-360-Outpaint-LTX2.3-IC-LoRA
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
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

export const icLoraTriggerHint = (preset: IcLoraPreset | null): string => {
    if (!preset?.triggerPhrase) {
        return "";
    }
    return `Prepend "${preset.triggerPhrase}" to your prompt`;
};
