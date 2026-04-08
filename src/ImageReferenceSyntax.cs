namespace VideoStages;

/// <summary>Parses user-facing <c>ImageReference</c> values that explicitly target an earlier stage.</summary>
public static class ImageReferenceSyntax
{
    private const string VideoStagePrefix = "Stage";
    private const string Base2EditStagePrefix = "edit";

    /// <summary>Returns <c>true</c> when <paramref name="rawValue"/> is a valid <c>StageN</c> reference.</summary>
    public static bool TryParseExplicitStageIndex(string rawValue, out int stageIndex)
    {
        stageIndex = -1;
        string compact = Compact(rawValue);
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        if (!compact.StartsWith(VideoStagePrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(compact[VideoStagePrefix.Length..], out int parsedIndex) || parsedIndex < 0)
        {
            stageIndex = -1;
            return false;
        }

        stageIndex = parsedIndex;
        return true;
    }

    /// <summary>Returns <c>true</c> when <paramref name="rawValue"/> is a valid <c>editN</c> reference.</summary>
    public static bool TryParseBase2EditStageIndex(string rawValue, out int stageIndex)
    {
        stageIndex = -1;
        string compact = Compact(rawValue);
        if (string.IsNullOrWhiteSpace(compact)
            || !compact.StartsWith(Base2EditStagePrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(compact[Base2EditStagePrefix.Length..], out int parsedIndex) || parsedIndex < 0)
        {
            return false;
        }

        stageIndex = parsedIndex;
        return true;
    }

    public static string FormatBase2EditStageIndex(int stageIndex) => $"{Base2EditStagePrefix}{stageIndex}";

    public static string Compact(string rawValue) => rawValue?.Trim().Replace(" ", "");
}
