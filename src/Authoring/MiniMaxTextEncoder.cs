namespace VideoStages.Authoring;

public enum MiniMaxTextEncoder
{
    Default,
    Qwen3Vl8B,
    Qwen3Vl4B,
}

internal static class MiniMaxTextEncoders
{
    internal const string FeatureFlag = "clipproj";

    private static readonly IReadOnlyList<(MiniMaxTextEncoder Encoder, string WireName)> Values =
    [
        (MiniMaxTextEncoder.Default, "default"),
        (MiniMaxTextEncoder.Qwen3Vl8B, "8b"),
        (MiniMaxTextEncoder.Qwen3Vl4B, "4b"),
    ];

    internal static MiniMaxTextEncoder Parse(string value)
    {
        string wireName = value?.Trim().ToLowerInvariant();
        return Values
            .FirstOrDefault(
                entry => entry.WireName == wireName,
                Values[0])
            .Encoder;
    }

    internal static string WireName(MiniMaxTextEncoder encoder) =>
        Values.FirstOrDefault(
                entry => entry.Encoder == encoder,
                Values[0])
            .WireName;

    internal static string RenderGeneratedTypeScript()
    {
        StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        Line("// Generated from MiniMaxTextEncoder.cs. Do not edit by hand.");
        Line();
        Line($"export const H3_TEXT_ENCODER_FEATURE = \"{FeatureFlag}\";");
        Line();
        Line("/** Every MiniMax H3 text encoder an authored clip may select. */");
        string values = string.Join(
            ", ",
            Values.Select(entry => $"\"{entry.WireName}\""));
        Line($"export const H3_TEXT_ENCODERS = [{values}] as const;");
        Line();
        Line("export type H3TextEncoder = (typeof H3_TEXT_ENCODERS)[number];");
        Line();
        string fallback = WireName(MiniMaxTextEncoder.Default);
        Line($"export const H3_TEXT_ENCODER_DEFAULT: H3TextEncoder = \"{fallback}\";");

        return result.ToString();
    }
}
