namespace VideoStages;

/// <summary>Parses and formats the authored stage-reference source syntax (<c>Stage3</c>, <c>edit2</c>).</summary>
public static class ImageReferenceSyntax
{
    private const string VideoStagePrefix = "Stage";
    private const string Base2EditStagePrefix = "edit";

    public static bool TryParseExplicitStageIndex(string rawValue, out int stageIndex) =>
        TryParseNonNegativeIndexAfterPrefix(StringUtils.Compact(rawValue), VideoStagePrefix, out stageIndex);

    public static bool TryParseBase2EditStageIndex(string rawValue, out int stageIndex) =>
        TryParseNonNegativeIndexAfterPrefix(StringUtils.Compact(rawValue), Base2EditStagePrefix, out stageIndex);

    public static string FormatExplicitStageIndex(int stageIndex) => $"{VideoStagePrefix}{stageIndex}";

    public static string FormatBase2EditStageIndex(int stageIndex) => $"{Base2EditStagePrefix}{stageIndex}";

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
