namespace VideoStages.Authoring;

public enum MiniMaxReferenceImageSize
{
    Match,
    Max,
}

internal static class MiniMaxReferenceImageSizes
{
    private static readonly IReadOnlyList<(MiniMaxReferenceImageSize Size, string WireName)> Values =
    [
        (MiniMaxReferenceImageSize.Match, "match"),
        (MiniMaxReferenceImageSize.Max, "max"),
    ];

    internal static MiniMaxReferenceImageSize Parse(string value)
    {
        string wireName = value?.Trim().ToLowerInvariant();
        return Values.FirstOrDefault(
            entry => entry.WireName == wireName,
            Values[0]).Size;
    }

    internal static string WireName(MiniMaxReferenceImageSize size) =>
        Values.FirstOrDefault(
            entry => entry.Size == size,
            Values[0]).WireName;

    internal static string RenderGeneratedTypeScript()
    {
        StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        Line("// Generated from MiniMaxReferenceImageSize.cs. Do not edit by hand.");
        Line();
        string values = string.Join(
            ", ",
            Values.Select(entry => $"\"{entry.WireName}\""));
        Line($"export const H3_REFERENCE_IMAGE_SIZES = [{values}] as const;");
        Line();
        Line("export type H3ReferenceImageSize = (typeof H3_REFERENCE_IMAGE_SIZES)[number];");
        Line();
        string fallback = WireName(MiniMaxReferenceImageSize.Match);
        Line($"export const H3_REFERENCE_IMAGE_SIZE_DEFAULT: H3ReferenceImageSize = \"{fallback}\";");

        return result.ToString();
    }
}
