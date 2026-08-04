namespace VideoStages.Architectures.Abstractions;

internal sealed record ArchitectureFeatureVocabularyEntry(
    ArchitectureFeature Feature,
    string WireName,
    string DisplayLabel);

/// <summary>
/// The single cross-language vocabulary for architecture features. <see cref="ArchitectureFeature"/>
/// remains the typed programming surface; this registry owns its one wire spelling and its labels.
/// </summary>
internal static class ArchitectureFeatureVocabulary
{
    internal static IReadOnlyList<ArchitectureFeatureVocabularyEntry> Features { get; } =
    [
        new(ArchitectureFeature.PromptRelay, "promptRelay", "Relay prompts"),
        new(ArchitectureFeature.FrameReferences, "frameReferences", "Frame references"),
        new(ArchitectureFeature.ReferenceFraming, "referenceFraming", "Reference framing"),
        new(ArchitectureFeature.Retake, "retake", "Retake"),
        new(
            ArchitectureFeature.AudioBoundaryCarry,
            "audioBoundaryCarry",
            "Boundary audio carry"),
        new(
            ArchitectureFeature.LatentUpscale,
            "latentUpscale",
            "Latent interpolation upscaling"),
        new(
            ArchitectureFeature.LatentModelUpscale,
            "latentModelUpscale",
            "Latent-model upscaling"),
        new(ArchitectureFeature.AudioReuse, "audioReuse", "Captured stage audio reuse"),
        new(
            ArchitectureFeature.AudioDerivedDuration,
            "audioDerivedDuration",
            "Audio-derived clip duration"),
        new(ArchitectureFeature.IcLora, "icLora", "IC-LoRA"),
    ];

    internal static string WireName(ArchitectureEntryMode mode) => mode switch
    {
        ArchitectureEntryMode.TextToVideo => "text-to-video",
        ArchitectureEntryMode.ImageToVideo => "image-to-video",
        ArchitectureEntryMode.InitVideo => "init-video",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static IEnumerable<string> WireNames(ArchitectureFeature features) =>
        Features
            .Where(entry => features.HasFlag(entry.Feature))
            .Select(entry => entry.WireName);

    internal static string WireName(ArchitectureFeature feature) =>
        Features.Single(entry => entry.Feature == feature).WireName;

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
        Line("export const AUTHORING_FEATURE_LABELS = {");
        foreach (ArchitectureFeatureVocabularyEntry feature in Features)
        {
            Line($"    {feature.WireName}: \"{feature.DisplayLabel}\",");
        }
        Line("} as const;");
        Line();
        Line("export type GeneratedAuthoringFeature = keyof typeof AUTHORING_FEATURE_LABELS;");

        return result.ToString();
    }
}
