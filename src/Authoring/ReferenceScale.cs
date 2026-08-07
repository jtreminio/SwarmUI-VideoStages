using System.Globalization;

namespace VideoStages.Authoring;

/// <summary>How much of a reference video's own resolution an authored document may keep.</summary>
internal static class ReferenceScale
{
    internal const double Full = 1;
    internal const double Half = 0.5;
    internal const double Quarter = 0.25;

    private static readonly double[] Admissible = [Full, Half, Quarter];

    internal static double Normalize(double scale) =>
        Admissible.Contains(scale) ? scale : Full;

    /// <summary>
    /// Renders the checked-in TypeScript projection. A backend test compares this byte for byte
    /// with <c>frontend/generatedReferenceScale.ts</c>. The option labels stay in TypeScript —
    /// they are presentation, not vocabulary.
    /// </summary>
    internal static string RenderGeneratedTypeScript()
    {
        StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        string values = string.Join(
            ", ",
            Admissible.Select(scale => scale.ToString(CultureInfo.InvariantCulture)));

        Line("// Generated from ReferenceScale.cs. Do not edit by hand.");
        Line();
        Line("/** The scale an unrecognized authored value falls back to. */");
        Line($"export const REFERENCE_SCALE_FULL = {Full.ToString(CultureInfo.InvariantCulture)};");
        Line();
        Line("/** Every reference scale an authored document may carry, in offer order. */");
        Line($"export const REFERENCE_SCALES = [{values}] as const;");

        return result.ToString();
    }
}
