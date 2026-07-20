import type { IcLoraControlType } from "./types";

// Curated LTX-2.3 IC-LoRA presets for the IC-LoRAs section's Preset dropdown: picking one seeds
// strength / control-type defaults and surfaces a trigger-phrase hint. Guidance only — presets never
// touch the prompt or gate behavior (any installed LoRA works via "Custom"), and the preset id rides
// in the JSON purely so the editor reopens with the same context.

export type IcLoraFamily =
    | "control-signal"
    | "effect"
    | "restoration"
    | "reference";

export interface IcLoraPreset {
    /** Stable id used as the Preset dropdown value. */
    id: string;
    displayName: string;
    /** HuggingFace repo the weights come from. */
    repoId: string;
    family: IcLoraFamily;
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
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Union-Control",
        family: "control-signal",
        triggerPhrase: "",
        strength: 1,
        controlType: "depth",
        note: "Structural control from depth/canny/normal signals; pick the control type to render.",
    },
    {
        id: "motion-track-control",
        displayName: "Motion Track Control",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control",
        family: "control-signal",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Guide motion with sparse point trajectories; feed a pre-rendered track video.",
    },
    {
        id: "in-outpainting",
        displayName: "In/Outpainting",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-In-Outpainting",
        family: "effect",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Fill or extend a masked clip; feed the masked video directly.",
    },
    {
        id: "ingredients",
        displayName: "Ingredients",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Ingredients",
        family: "reference",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Consistent characters/props from a reference sheet; feed the sheet as the drive video.",
    },
    {
        id: "lipdub",
        displayName: "LipDub",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-LipDub",
        family: "effect",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "New lip movements matching target audio; pair with this clip's audio track.",
    },
    {
        id: "hdr",
        displayName: "HDR",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-HDR",
        family: "effect",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "16-bit HDR (LogC3) generation; works with no drive video (LoRA-only).",
    },
    {
        id: "pixel-spatial-upscaler",
        displayName: "Pixel Spatial Upscaler",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler",
        family: "restoration",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Creative 2×/4× upscale; feed the low-res clip directly.",
    },
    {
        id: "deblur",
        displayName: "Deblur",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Deblur",
        family: "restoration",
        triggerPhrase: "DEBLUR",
        strength: 1,
        controlType: "none",
        note: "Feed the blurry clip directly. Lower toward 0.8 if over-sharpened.",
    },
    {
        id: "decompression",
        displayName: "Decompression",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Decompression",
        family: "restoration",
        triggerPhrase: "ENHANCE QUALITY",
        strength: 1,
        controlType: "none",
        note: "Removes compression artifacts; feed a low-bitrate clip directly.",
    },
    {
        id: "water-simulation",
        displayName: "Water Simulation",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Water-Simulation",
        family: "effect",
        triggerPhrase: "ADD WATER",
        strength: 1.2,
        controlType: "none",
        note: "Sweet spot ~1.2 (1.0 subtle; ≥1.5 warps faces). Feed a dry clip.",
    },
    {
        id: "instant-shave",
        displayName: "Instant Shave",
        repoId: "Lightricks/LTX-2.3-22b-IC-LoRA-Instant-Shave",
        family: "effect",
        triggerPhrase: "REMOVEBEARD",
        strength: 1,
        controlType: "none",
        note: "Feed a bearded clip directly. Lower toward 0.8 if artifacts appear.",
    },
    {
        id: "colorizer",
        displayName: "Colorizer",
        repoId: "DoctorDiffusion/LTX-2.3-IC-LoRA-Colorizer",
        family: "restoration",
        triggerPhrase: "COLORIZE",
        strength: 1,
        controlType: "none",
        note: "Community. Colorizes black & white footage; feed the grayscale clip. Confirm trigger in README.",
    },
    {
        id: "restyle",
        displayName: "ReStyle",
        repoId: "Cseti/LTX2.3-22B_ReStyle_IC-LoRA",
        family: "effect",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Style transfer over an existing clip; see README for style prompts.",
    },
    {
        id: "cameraman",
        displayName: "Cameraman",
        repoId: "Cseti/LTX2.3-22B_IC-LoRA-Cameraman_v2",
        family: "control-signal",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Camera-motion control driven by the reference video's movement.",
    },
    {
        id: "crossview-prompt",
        displayName: "CrossView Prompt",
        repoId: "Cseti/LTX2.3-22B_IC-LoRA-CrossView-Prompt",
        family: "reference",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Re-renders the scene from a prompted new camera viewpoint.",
    },
    {
        id: "outpaint",
        displayName: "Outpaint",
        repoId: "oumoumad/LTX-2.3-22b-IC-LoRA-Outpaint",
        family: "effect",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Extends the frame beyond the source video's borders.",
    },
    {
        id: "refocus",
        displayName: "ReFocus",
        repoId: "oumoumad/LTX-2.3-22b-IC-LoRA-ReFocus",
        family: "restoration",
        triggerPhrase: "",
        strength: 1,
        controlType: "none",
        note: "Community. Fixes lens blur / refocuses; feed the blurred clip directly.",
    },
    {
        id: "vr360-outpaint",
        displayName: "VR 360 Outpaint",
        repoId: "TheBurgstall/VR-360-Outpaint-LTX2.3-IC-LoRA",
        family: "effect",
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
