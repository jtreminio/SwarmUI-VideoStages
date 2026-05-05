namespace VideoStages;

public static class StringUtils
{
    public static string Compact(string rawValue)
    {
        if (rawValue is null)
        {
            return string.Empty;
        }
        return rawValue.Trim().Replace(" ", "");
    }

    public static bool Equals(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
