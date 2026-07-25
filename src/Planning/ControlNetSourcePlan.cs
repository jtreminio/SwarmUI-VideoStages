namespace VideoStages.Planning;

/// <summary>Parses the finite ControlNet source vocabulary into its zero-based planned index.</summary>
internal static class ControlNetSourcePlan
{
    internal static bool TryParseIndex(string source, out int index)
    {
        string compact = StringUtils.Compact(source);
        if (compact.StartsWith("ControlNet", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(compact.AsSpan("ControlNet".Length), out int oneBased)
            && oneBased is >= 1 and <= 3)
        {
            index = oneBased - 1;
            return true;
        }
        index = -1;
        return false;
    }
}
