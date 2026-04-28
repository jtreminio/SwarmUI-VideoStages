namespace VideoStages;

public static class ImageReferenceSyntax
{
    private const string VideoStagePrefix = "Stage";
    private const string Base2EditStagePrefix = "edit";

    public static bool TryParseExplicitStageIndex(string rawValue, out int stageIndex)
    {
        return TryParseNonNegativeIndexAfterPrefix(Compact(rawValue), VideoStagePrefix, out stageIndex);
    }

    public static bool TryParseBase2EditStageIndex(string rawValue, out int stageIndex)
    {
        return TryParseNonNegativeIndexAfterPrefix(Compact(rawValue), Base2EditStagePrefix, out stageIndex);
    }

    public static string FormatBase2EditStageIndex(int stageIndex) => $"{Base2EditStagePrefix}{stageIndex}";

    public static string Compact(string rawValue)
    {
        if (rawValue is null)
        {
            return string.Empty;
        }
        return rawValue.Trim().Replace(" ", "");
    }

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
