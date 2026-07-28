namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Declarative LTX dimension requirements for curated IC-LoRA weights. Planning resolves this
/// once; runtime dimension code consumes only the typed factor recorded in <see cref="IcLoraPlan"/>.
/// </summary>
internal static class IcLoraDimensionPolicyResolver
{
    private static readonly IReadOnlyDictionary<string, int> PresetFactors =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["union-control"] = 2,
            ["motion-track-control"] = 2,
            ["pixel-spatial-upscaler-x2"] = 2,
            ["pixel-spatial-upscaler-x4"] = 4,
        };

    private static readonly IReadOnlyDictionary<string, int> CuratedModelFactors =
        BuildCuratedModelFactors();

    private static Dictionary<string, int> BuildCuratedModelFactors()
    {
        Dictionary<string, int> factors = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string preset, int factor) in PresetFactors)
        {
            factors[NormalizeModelName(IcLoraWeights.ModelNameFor(preset))] = factor;
            factors[NormalizeModelName(IcLoraWeights.LegacyModelNameFor(preset))] = factor;
        }
        return factors;
    }

    internal static int Resolve(string preset, string modelName)
    {
        string presetId = $"{preset}".Trim();
        if (PresetFactors.TryGetValue(presetId, out int presetFactor))
        {
            return presetFactor;
        }

        string normalizedModel = NormalizeModelName(modelName);
        return CuratedModelFactors.TryGetValue(normalizedModel, out int modelFactor)
            ? modelFactor
            : 1;
    }

    private static string NormalizeModelName(string modelName)
    {
        string normalized = $"{modelName}".Trim().Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        if (slash >= 0)
        {
            normalized = normalized[(slash + 1)..];
        }
        return normalized.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^".safetensors".Length]
            : normalized;
    }
}
