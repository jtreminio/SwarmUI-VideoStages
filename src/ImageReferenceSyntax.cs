namespace VideoStages;

/// <summary>Parses user-facing <c>ImageReference</c> values that explicitly target an earlier stage.</summary>
public static class ImageReferenceSyntax
{
    /// <summary>Returns <c>true</c> when <paramref name="rawValue"/> is a valid <c>StageN</c> reference.</summary>
    public static bool TryParseExplicitStageIndex(string rawValue, out int stageIndex)
    {
        stageIndex = -1;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        string compact = rawValue.Trim().Replace(" ", "");
        if (!compact.StartsWith("Stage", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(compact["Stage".Length..], out int parsedIndex) || parsedIndex < 0)
        {
            stageIndex = -1;
            return false;
        }

        stageIndex = parsedIndex;
        return true;
    }
}
