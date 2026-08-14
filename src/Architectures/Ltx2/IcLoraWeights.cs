namespace VideoStages.Architectures.Ltx2;

internal sealed record IcLoraWeight(string Url, int DimensionDownscaleFactor = 1);

/// <summary>Curated IC-LoRA weight downloads and their intrinsic dimension requirements.</summary>
public static class IcLoraWeights
{
    private const string HF = "https://huggingface.co";
    public const string AutoModelToken = "[AUTO]";
    public const string AutoModelFolder = "LTX-2/IC-LoRA";
    internal const string Da3ModelFileName = "depth_anything_3_mono_large.safetensors";
    internal const string MoGeModelFileName = "moge_2_vitl_normal_fp16.safetensors";

    internal static readonly IReadOnlyDictionary<string, IcLoraWeight> Weights =
        new Dictionary<string, IcLoraWeight>()
    {
        ["union-control"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Union-Control/resolve/main/ltx-2.3-22b-ic-lora-union-control-ref0.5.safetensors", 2),
        ["motion-track-control"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Motion-Track-Control/resolve/main/ltx-2.3-22b-ic-lora-motion-track-control-ref0.5.safetensors", 2),
        ["in-outpainting"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-In-Outpainting/resolve/main/ltx-2.3-22b-ic-lora-in-outpainting-0.9.safetensors"),
        ["ingredients"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.3-22b-ic-lora-ingredients-0.9.safetensors"),
        ["lipdub"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-LipDub/resolve/main/ltx-2.3-22b-ic-lora-lipdub-0.9.safetensors"),
        ["pixel-spatial-upscaler-x2"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x2-0.9.safetensors", 2),
        ["pixel-spatial-upscaler-2.5-x2"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.5-22b-ic-lora-pixel-spatial-upscaler-x2-1.0.safetensors", 2),
        ["pixel-spatial-upscaler-x4"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.3-22b-ic-lora-pixel-spatial-upscaler-x4-0.9.safetensors", 4),
        ["deblur"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Deblur/resolve/main/ltx-2.3-22b-ic-lora-deblur-0.9.safetensors"),
        ["decompression"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Decompression/resolve/main/ltx-2.3-22b-ic-lora-decompression-0.9.safetensors"),
        ["water-simulation"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Water-Simulation/resolve/main/ltx-2.3-22b-ic-lora-water-simulation-0.9.safetensors"),
        ["instant-shave"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Instant-Shave/resolve/main/ltx-2.3-22b-ic-lora-instant-shave-0.9.safetensors"),
        ["colorization"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Colorization/resolve/main/ltx-2.3-22b-ic-lora-colorization-0.9.safetensors"),
        ["cross-eyed"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Cross-Eyed/resolve/main/ltx-2.3-22b-ic-lora-cross-eyed-0.9.safetensors"),
        ["day-to-night"] = new($"{HF}/Lightricks/LTX-2.3-22b-IC-LoRA-Day-To-Night/resolve/main/ltx-2.3-22b-ic-lora-day-to-night-0.9.safetensors"),
        ["restyle"] = new($"{HF}/Cseti/LTX2.3-22B_ReStyle_IC-LoRA/resolve/main/852654_LTX2.3-22B_ReStyle_IC-LoRA_8000_v0.1.safetensors"),
        ["cameraman"] = new($"{HF}/Cseti/LTX2.3-22B_IC-LoRA-Cameraman_v2/resolve/main/LTX2.3-22B_IC-LoRA-Cameraman_v2_14000.safetensors"),
        ["crossview-prompt"] = new($"{HF}/Cseti/LTX2.3-22B_IC-LoRA-CrossView-Prompt/resolve/main/LTX2.3-22B_IC-LoRA-CrossView-Prompt_v0.9_13700.safetensors"),
        ["outpaint"] = new($"{HF}/oumoumad/LTX-2.3-22b-IC-LoRA-Outpaint/resolve/main/ltx-2.3-22b-ic-lora-outpaint.safetensors"),
        ["refocus"] = new($"{HF}/oumoumad/LTX-2.3-22b-IC-LoRA-ReFocus/resolve/main/ltx-2.3-22b-ic-lora-refocus.safetensors"),
        ["vr360-outpaint"] = new($"{HF}/TheBurgstall/VR-360-Outpaint-LTX2.3-IC-LoRA/resolve/main/360vroutpaint_v2_step09000.safetensors"),
    };

    /// <summary>
    /// Renders the checked-in TypeScript projection. A backend test compares this byte for byte
    /// with <c>frontend/architectures/ltx2/generatedIcLora.ts</c>. The presets' labels, notes and
    /// trigger phrases stay in TypeScript — they are presentation, not vocabulary.
    /// </summary>
    internal static string RenderGeneratedTypeScript()
    {
        StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        Line("// Generated from IcLoraWeights.cs. Do not edit by hand.");
        Line();
        Line("/** The model name that means \"download this preset's own weights\". */");
        Line($"export const IC_LORA_AUTO = \"{AutoModelToken}\";");
        Line();
        Line("/** Where auto-downloaded IC-LoRA weights land under the models root. */");
        Line($"export const IC_LORA_AUTO_FOLDER = \"{AutoModelFolder}\";");
        Line();
        Line("/** Every curated preset's weights, in offer order. */");
        Line("export const IC_LORA_WEIGHTS = [");
        foreach ((string id, IcLoraWeight weight) in Weights)
        {
            Line("    {");
            Line($"        id: \"{id}\",");
            // Biome wraps a property onto its own line once it passes its 80-column width.
            string url = $"        weightsUrl: \"{weight.Url}\",";
            Line(url.Length <= 80 ? url : $"        weightsUrl:\n            \"{weight.Url}\",");
            Line($"        dimensionDownscaleFactor: {weight.DimensionDownscaleFactor},");
            Line("    },");
        }
        Line("] as const;");

        return result.ToString();
    }

    public static string FileStem(string url)
    {
        string file = url[(url.LastIndexOf('/') + 1)..];
        return file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            ? file[..^".safetensors".Length]
            : file;
    }

    public static string ModelNameFor(string presetId)
        => Weights.TryGetValue($"{presetId}".Trim(), out IcLoraWeight weight)
            ? $"{AutoModelFolder}/{FileStem(weight.Url).Replace('.', '_')}"
            : null;

    public static string LegacyModelNameFor(string presetId)
        => Weights.TryGetValue($"{presetId}".Trim(), out IcLoraWeight weight)
            ? $"{AutoModelFolder}/{FileStem(weight.Url)}"
            : null;
}
