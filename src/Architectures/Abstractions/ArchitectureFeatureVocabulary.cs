namespace VideoStages.Architectures.Abstractions;

internal sealed record ArchitectureFeatureVocabularyEntry(
    ArchitectureFeature Feature,
    string WireName,
    string AuthoringKey,
    string DisplayLabel);

internal sealed record ConditionalRuleCodeVocabularyEntry(
    ConditionalRuleCodeId Id,
    string Code);

/// <summary>
/// The single cross-language vocabulary for architecture features. <see cref="ArchitectureFeature"/>
/// remains the typed programming surface; this registry owns its wire names, authoring keys, and
/// labels.
/// </summary>
internal static class ArchitectureFeatureVocabulary
{
    internal static IReadOnlyList<ArchitectureFeatureVocabularyEntry> Features { get; } =
    [
        new(
            ArchitectureFeature.PromptRelay,
            "prompt-relay",
            "promptRelay",
            "Relay prompts"),
        new(
            ArchitectureFeature.FrameReferences,
            "frame-references",
            "frameReferences",
            "Frame references"),
        new(
            ArchitectureFeature.ReferenceFraming,
            "reference-framing",
            "referenceFraming",
            "Reference framing"),
        new(
            ArchitectureFeature.Retake,
            "retake",
            "retake",
            "Retakes"),
        new(
            ArchitectureFeature.ClipAudio,
            "audio-sources",
            "clipAudio",
            "Clip audio"),
        new(
            ArchitectureFeature.AudioSegments,
            "audio-segments",
            "audioSegments",
            "Audio segments"),
        new(
            ArchitectureFeature.AudioReuse,
            "audio-reuse",
            "audioReuse",
            "Captured stage audio reuse"),
        new(
            ArchitectureFeature.AudioDerivedDuration,
            "audio-derived-duration",
            "audioDerivedDuration",
            "Audio-derived clip duration"),
        new(
            ArchitectureFeature.ControlSignalDerivedDuration,
            "control-signal-derived-duration",
            "controlSignalDerivedDuration",
            "Control-signal-derived clip duration"),
        new(
            ArchitectureFeature.IcLora,
            "ic-lora",
            "icLora",
            "IC-LoRA"),
    ];

    internal static IReadOnlyList<ConditionalRuleCodeVocabularyEntry>
        ConditionalRuleCodes
    { get; } =
    [
        new(ConditionalRuleCodeId.RetakeRequiresSource, "retake-source-required"),
    ];

    internal static string WireName(ArchitectureEntryMode mode) => mode switch
    {
        ArchitectureEntryMode.TextToVideo => "text-to-video",
        ArchitectureEntryMode.ImageToVideo => "image-to-video",
        ArchitectureEntryMode.InitVideo => "init-video",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static string RuleCode(ConditionalRuleCodeId id) =>
        ConditionalRuleCodes.Single(entry => entry.Id == id).Code;

    internal static IEnumerable<string> WireNames(ArchitectureFeature features) =>
        Features
            .Where(entry => features.HasFlag(entry.Feature))
            .Select(entry => entry.WireName);

    internal static string AuthoringKey(ArchitectureFeature feature) =>
        Features.Single(entry => entry.Feature == feature).AuthoringKey;

    /// <summary>
    /// Renders the checked-in TypeScript projection. A backend test compares this output byte for
    /// byte with <c>frontend/architectures/generatedFeatures.ts</c>.
    /// </summary>
    internal static string RenderGeneratedTypeScript()
    {
        StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        Line("// Generated from ArchitectureFeatureVocabulary.cs. Do not edit by hand.");
        Line();
        Line("export const AUTHORING_FEATURE_WIRE_NAMES = {");
        foreach (ArchitectureFeatureVocabularyEntry feature in Features)
        {
            Line($"    {feature.AuthoringKey}: {Quote(feature.WireName)},");
        }
        Line("} as const;");
        Line();
        Line("export type GeneratedAuthoringFeature =");
        Line("    keyof typeof AUTHORING_FEATURE_WIRE_NAMES;");
        Line();
        Line("export const AUTHORING_FEATURE_LABELS: Record<");
        Line("    GeneratedAuthoringFeature,");
        Line("    string");
        Line("> = {");
        foreach (ArchitectureFeatureVocabularyEntry feature in Features)
        {
            Line($"    {feature.AuthoringKey}: {Quote(feature.DisplayLabel)},");
        }
        Line("};");
        Line();
        Line("export const CONDITIONAL_RULE_CODES = {");
        foreach (ConditionalRuleCodeVocabularyEntry rule in ConditionalRuleCodes)
        {
            Line($"    {CamelCase(rule.Id.ToString())}: {Quote(rule.Code)},");
        }
        Line("} as const;");
        Line();
        Line("export type GeneratedConditionalRuleCode =");
        Line("    (typeof CONDITIONAL_RULE_CODES)[keyof typeof CONDITIONAL_RULE_CODES];");

        return result.ToString();
    }

    private static string CamelCase(string value) =>
        char.ToLowerInvariant(value[0]) + value[1..];

    private static string Quote(string value) =>
        $"\"{value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)}\"";
}
