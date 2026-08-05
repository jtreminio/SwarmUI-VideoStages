namespace VideoStages.Planning;

internal static class ControlNetSourcePlan
{
    internal static bool TryParseIndex(string source, out int index)
    {
        string compact = StringUtils.Compact(source);
        if (compact.StartsWith("ControlNet", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(compact.AsSpan("ControlNet".Length), out int oneBased)
            && ControlNetCoreMediaCapture.IsValidIndex(oneBased - 1))
        {
            index = oneBased - 1;
            return true;
        }
        index = -1;
        return false;
    }

    internal static string Format(int index)
    {
        if (!ControlNetCoreMediaCapture.IsValidIndex(index))
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return $"ControlNet {index + 1}";
    }
}
