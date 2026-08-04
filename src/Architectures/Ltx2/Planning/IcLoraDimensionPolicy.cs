namespace VideoStages.Architectures.Ltx2.Planning;

internal static class IcLoraDimensionPolicyResolver
{
    private static readonly IReadOnlyDictionary<string, int> CuratedModelFactors =
        BuildCuratedModelFactors();

    private static Dictionary<string, int> BuildCuratedModelFactors()
    {
        Dictionary<string, int> factors = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string preset, IcLoraWeight weight) in IcLoraWeights.Weights)
        {
            if (weight.DimensionDownscaleFactor > 1)
            {
                factors[NormalizeModelName(IcLoraWeights.ModelNameFor(preset))] =
                    weight.DimensionDownscaleFactor;
                factors[NormalizeModelName(IcLoraWeights.LegacyModelNameFor(preset))] =
                    weight.DimensionDownscaleFactor;
            }
        }
        return factors;
    }

    internal static int Resolve(string preset, string modelName)
    {
        string presetId = $"{preset}".Trim().ToLowerInvariant();
        if (IcLoraWeights.Weights.TryGetValue(presetId, out IcLoraWeight presetWeight)
            && presetWeight.DimensionDownscaleFactor > 1)
        {
            return presetWeight.DimensionDownscaleFactor;
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
