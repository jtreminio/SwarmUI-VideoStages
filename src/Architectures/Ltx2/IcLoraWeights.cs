namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Server-side table of the curated IC-LoRA presets' weight downloads. Mirrors the preset ids and
/// weightsUrl values in frontend/icLoraPresets.ts — both sides derive the installed model name
/// from the same URL basename, so they agree on where "[AUTO]" weights land without exchanging
/// names. Keep the two lists in sync.
/// </summary>
public static class IcLoraWeights
{
    private const string HF = "https://huggingface.co";
    public const string AutoModelToken = "[AUTO]";
    public const string AutoModelFolder = "LTX-2/IC-LoRA";
    internal const string Da3ModelFileName = "depth_anything_3_mono_large.safetensors";
    internal const string MoGeModelFileName = "moge_2_vitl_normal_fp16.safetensors";

    /// <summary>Preset id → direct safetensors URL; downloads keep the URL's original filename.</summary>
    public static readonly IReadOnlyDictionary<string, string> Urls = new Dictionary<string, string>()
    {
        ["union-control"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Union-Control/resolve/main/ltx-2.3-22b-ic-lora-union-control-ref0.5.safetensors",
        ["motion-track-control"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control/resolve/main/ltx-2.3-22b-ic-lora-motion-track-control-ref0.5.safetensors",
        ["in-outpainting"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-In-Outpainting/resolve/main/ltx-2.3-22b-ic-lora-in-outpainting-0.9.safetensors",
        ["ingredients"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.3-22b-ic-lora-ingredients-0.9.safetensors",
        ["lipdub"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-LipDub/resolve/main/ltx-2.3-22b-ic-lora-lipdub-0.9.safetensors",
        ["hdr"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-HDR/resolve/main/ltx-2.3-22b-ic-lora-hdr-0.9.safetensors",
        ["pixel-spatial-upscaler-x2"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x2-0.9.safetensors",
        ["pixel-spatial-upscaler-x4"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors",
        ["deblur"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Deblur/resolve/main/ltx-2.3-22b-ic-lora-deblur-0.9.safetensors",
        ["decompression"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Decompression/resolve/main/ltx-2.3-22b-ic-lora-decompression-0.9.safetensors",
        ["water-simulation"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Water-Simulation/resolve/main/ltx-2.3-22b-ic-lora-water-simulation-0.9.safetensors",
        ["instant-shave"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Instant-Shave/resolve/main/ltx-2.3-22b-ic-lora-instant-shave-0.9.safetensors",
        ["colorization"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Colorization/resolve/main/ltx-2.3-22b-ic-lora-colorization-0.9.safetensors",
        ["cross-eyed"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Cross-Eyed/resolve/main/ltx-2.3-22b-ic-lora-cross-eyed-0.9.safetensors",
        ["day-to-night"] = $"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Day-To-Night/resolve/main/ltx-2.3-22b-ic-lora-day-to-night-0.9.safetensors",
        ["restyle"] = $"{HF}/Cseti/LTX2.3-22B_ReStyle_IC-LoRA/resolve/main/852654_LTX2.3-22B_ReStyle_IC-LoRA_8000_v0.1.safetensors",
        ["cameraman"] = $"{HF}/Cseti/LTX2.3-22B_IC-LoRA-Cameraman_v2/resolve/main/LTX2.3-22B_IC-LoRA-Cameraman_v2_14000.safetensors",
        ["crossview-prompt"] = $"{HF}/Cseti/LTX2.3-22B_IC-LoRA-CrossView-Prompt/resolve/main/LTX2.3-22B_IC-LoRA-CrossView-Prompt_v0.9_13700.safetensors",
        ["outpaint"] = $"{HF}/oumoumad/LTX-2.3-22b-IC-LoRA-Outpaint/resolve/main/ltx-2.3-22b-ic-lora-outpaint.safetensors",
        ["refocus"] = $"{HF}/oumoumad/LTX-2.3-22b-IC-LoRA-ReFocus/resolve/main/ltx-2.3-22b-ic-lora-refocus.safetensors",
        ["vr360-outpaint"] = $"{HF}/TheBurgstall/VR-360-Outpaint-LTX2.3-IC-LoRA/resolve/main/360vroutpaint_v2_step09000.safetensors",
    };

    /// <summary>The file stem (URL basename minus ".safetensors") a preset's download saves as.</summary>
    public static string FileStem(string url)
    {
        string file = url[(url.LastIndexOf('/') + 1)..];
        return file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            ? file[..^".safetensors".Length]
            : file;
    }

    /// <summary>The LoRA model name an [AUTO] entry with this preset resolves to, or null for unknown presets.</summary>
    public static string ModelNameFor(string presetId)
        => Urls.TryGetValue($"{presetId}".Trim(), out string url)
            ? $"{AutoModelFolder}/{FileStem(url)}"
            : null;
}
