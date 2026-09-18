// Generated from IcLoraWeights.cs. Do not edit by hand.

/** The model name that means "download this preset's own weights". */
export const IC_LORA_AUTO = "[AUTO]";

/** Where auto-downloaded IC-LoRA weights land under the models root. */
export const IC_LORA_AUTO_FOLDER = "LTX-2/IC-LoRA";

export const IC_LORA_WEIGHTS = [
    {
        id: "ingredients",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.5-22b-ic-lora-ingredients-0.9.safetensors",
        dimensionDownscaleFactor: 1,
    },
    {
        id: "detailer",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2-19b-IC-LoRA-Detailer/resolve/main/ltx-2-19b-ic-lora-detailer.safetensors",
        dimensionDownscaleFactor: 1,
    },
    {
        id: "pixel-spatial-upscaler-2.5-x2",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.5-22b-ic-lora-pixel-spatial-upscaler-x2-1.0.safetensors",
        dimensionDownscaleFactor: 2,
    },
    {
        id: "deblur",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Deblur/resolve/main/ltx-2.5-22b-ic-lora-deblur-0.9.safetensors",
        dimensionDownscaleFactor: 1,
    },
    {
        id: "decompression",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Decompression/resolve/main/ltx-2.5-22b-ic-lora-decompression-0.9.safetensors",
        dimensionDownscaleFactor: 1,
    },
    {
        id: "water-simulation",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Water-Simulation/resolve/main/ltx-2.5-22b-ic-lora-water-simulation-0.9.safetensors",
        dimensionDownscaleFactor: 1,
    },
    {
        id: "colorization",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Colorization/resolve/main/ltx-2.5-22b-ic-lora-colorization-0.9.safetensors",
        dimensionDownscaleFactor: 1,
    },
    {
        id: "day-to-night",
        weightsUrl:
            "https://huggingface.co/Lightricks/LTX-2.5-22b-IC-LoRA-Day-To-Night/resolve/main/ltx-2.5-22b-ic-lora-day-to-night-0.9.safetensors",
        dimensionDownscaleFactor: 1,
    },
] as const;
