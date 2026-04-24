namespace VideoStages;

/// <summary>Parses user-facing <c>ImageReference</c> values that explicitly target an earlier stage.</summary>
public static class ImageReferenceSyntax
{
    private const string VideoStagePrefix = "Stage";
    private const string Base2EditStagePrefix = "edit";

    public static bool TryParseExplicitStageIndex(string rawValue, out int stageIndex)
    {
        stageIndex = -1;
        string compact = Compact(rawValue);
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        if (!compact.StartsWith(VideoStagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(compact[VideoStagePrefix.Length..], out int parsedIndex) || parsedIndex < 0)
        {
            return false;
        }

        stageIndex = parsedIndex;
        return true;
    }

    public static bool TryParseBase2EditStageIndex(string rawValue, out int stageIndex)
    {
        stageIndex = -1;
        string compact = Compact(rawValue);
        if (string.IsNullOrWhiteSpace(compact)
            || !compact.StartsWith(Base2EditStagePrefix, StringComparison.OrdinalIgnoreCase))
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
