namespace VideoStages.Authoring;

/// <summary>Parses and formats the authored stage-reference source syntax (<c>Stage3</c>).</summary>
public static class ImageReferenceSyntax
{
    private const string VideoStagePrefix = "Stage";

    public static bool TryParseExplicitStageIndex(string rawValue, out int stageIndex) =>
        TryParseNonNegativeIndexAfterPrefix(StringUtils.Compact(rawValue), VideoStagePrefix, out stageIndex);

    public static bool IsHostStageSource(string rawValue) =>
        StringUtils.Equals(rawValue, MediaSource.Base)
        || StringUtils.Equals(rawValue, MediaSource.Refiner)
        || MediaSource.TryParseBase2EditIndex(rawValue, out _);

    public static string FormatExplicitStageIndex(int stageIndex) => $"{VideoStagePrefix}{stageIndex}";

    private static bool TryParseNonNegativeIndexAfterPrefix(string compact, string prefix, out int stageIndex)
    {
        stageIndex = -1;
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }
        if (!compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!int.TryParse(compact.AsSpan(prefix.Length), out int parsedIndex) || parsedIndex < 0)
        {
            return false;
        }
        stageIndex = parsedIndex;
        return true;
    }
}
