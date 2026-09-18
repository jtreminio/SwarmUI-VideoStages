namespace VideoStages.Architectures.Ltx2;

internal sealed record IcLoraWeight(string Url, int DimensionDownscaleFactor = 1);

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
        ["ingredients"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Ingredients/resolve/main/ltx-2.5-22b-ic-lora-ingredients-0.9.safetensors"),
        ["detailer"] = new($"{HF}/Lightricks/LTX-2-19b-IC-LoRA-Detailer/resolve/main/ltx-2-19b-ic-lora-detailer.safetensors"),
        ["pixel-spatial-upscaler-2.5-x2"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Pixel-Spatial-Upscaler/resolve/main/ltx-2.5-22b-ic-lora-pixel-spatial-upscaler-x2-1.0.safetensors", 2),
        ["deblur"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Deblur/resolve/main/ltx-2.5-22b-ic-lora-deblur-0.9.safetensors"),
        ["decompression"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Decompression/resolve/main/ltx-2.5-22b-ic-lora-decompression-0.9.safetensors"),
        ["water-simulation"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Water-Simulation/resolve/main/ltx-2.5-22b-ic-lora-water-simulation-0.9.safetensors"),
        ["colorization"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Colorization/resolve/main/ltx-2.5-22b-ic-lora-colorization-0.9.safetensors"),
        ["day-to-night"] = new($"{HF}/Lightricks/LTX-2.5-22b-IC-LoRA-Day-To-Night/resolve/main/ltx-2.5-22b-ic-lora-day-to-night-0.9.safetensors"),
    };

    // Drift-tested against frontend/architectures/ltx2/generatedIcLora.ts.
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
