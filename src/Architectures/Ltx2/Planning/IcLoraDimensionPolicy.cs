namespace VideoStages.Architectures.Ltx2.Planning;

internal static class IcLoraDimensionPolicy
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

    private static string NormalizeModelName(string modelName) =>
        IcLoraWeights.FileStem($"{modelName}".Trim().Replace('\\', '/'));
}
