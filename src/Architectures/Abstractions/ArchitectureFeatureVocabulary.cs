using VideoStages.Planning;

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
        new(ArchitectureFeature.ClipReferences, "clipReferences", "Clip references"),
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

    internal static string WireName(BoundaryJoinType join) => join switch
    {
        BoundaryJoinType.Cut => "cut",
        BoundaryJoinType.Continue => "continue",
        BoundaryJoinType.Crossfade => "crossfade",
        _ => throw new ArgumentOutOfRangeException(nameof(join)),
    };

    internal static string WireName(RuleSupport support) => support switch
    {
        RuleSupport.Supported => "supported",
        RuleSupport.Unsupported => "unsupported",
        RuleSupport.Conditional => "conditional",
        _ => throw new ArgumentOutOfRangeException(nameof(support)),
    };

    internal static string WireName(ContinueBoundaryMode mode) => mode switch
    {
        ContinueBoundaryMode.Overlap => "overlap",
        ContinueBoundaryMode.Reference => "reference",
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
        Line("export const ARCHITECTURE_FEATURE_LABELS = {");
        foreach (ArchitectureFeatureVocabularyEntry feature in Features)
        {
            Line($"    {feature.WireName}: \"{feature.DisplayLabel}\",");
        }
        Line("} as const;");
        Line();
        Line("export type GeneratedArchitectureFeature =");
        Line("    keyof typeof ARCHITECTURE_FEATURE_LABELS;");
        Line();
        Line("/** The entry modes a generated clip can take; a sourced clip is always init-video. */");
        Line("export type GeneratedEntryMode ="
            + $" \"{WireName(ArchitectureEntryMode.TextToVideo)}\""
            + $" | \"{WireName(ArchitectureEntryMode.ImageToVideo)}\";");
        Line();
        Line("/** Every entry mode the serialized catalog can carry, in declaration order. */");
        StringList(
            "ENTRY_MODES",
            [.. Enum.GetValues<ArchitectureEntryMode>().Select(WireName)]);
        Line();
        Line("/** Every boundary join the serialized catalog can carry, in declaration order. */");
        StringList(
            "BOUNDARY_MODES",
            [.. Enum.GetValues<BoundaryJoinType>().Select(WireName)]);
        Line();
        Line("/** Every rule support the serialized catalog can carry, in declaration order. */");
        StringList(
            "RULE_SUPPORTS",
            [.. Enum.GetValues<RuleSupport>().Select(WireName)]);
        Line();
        Line("/** Every continue mode the serialized catalog can carry, in declaration order. */");
        StringList(
            "CONTINUE_MODES",
            [.. Enum.GetValues<ContinueBoundaryMode>().Select(WireName)]);

        // Biome keeps a const array on one line while it fits its 80-column width.
        void StringList(string name, IReadOnlyList<string> values)
        {
            string quoted = string.Join(", ", values.Select(value => $"\"{value}\""));
            string single = $"export const {name} = [{quoted}] as const;";
            if (single.Length <= 80)
            {
                Line(single);
                return;
            }
            Line($"export const {name} = [");
            foreach (string value in values)
            {
                Line($"    \"{value}\",");
            }
            Line("] as const;");
        }

        return result.ToString();
    }
}
