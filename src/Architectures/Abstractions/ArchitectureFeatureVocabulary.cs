namespace VideoStages.Architectures.Abstractions;

internal enum CapabilityVocabularyScope
{
    Architecture,
    Clip,
    Stage,
}

internal sealed record CapabilityVocabularyEntry(
    ArchitectureCapability Architecture,
    ClipCapability Clip,
    StageCapability Stage,
    string WireName,
    string UpscaleModeWireName = null)
{
    internal CapabilityVocabularyScope Scope =>
        Architecture != ArchitectureCapability.None
            ? CapabilityVocabularyScope.Architecture
            : Clip != ClipCapability.None
                ? CapabilityVocabularyScope.Clip
                : CapabilityVocabularyScope.Stage;

    internal string SymbolName => Scope switch
    {
        CapabilityVocabularyScope.Architecture => Architecture.ToString(),
        CapabilityVocabularyScope.Clip => Clip.ToString(),
        CapabilityVocabularyScope.Stage => Stage.ToString(),
        _ => throw new ArgumentOutOfRangeException(),
    };

    internal bool Supports(ArchitectureCapability value) =>
        Architecture != ArchitectureCapability.None
        && value.HasFlag(Architecture);

    internal bool Supports(ClipCapability value) =>
        Clip != ClipCapability.None
        && value.HasFlag(Clip);

    internal bool Supports(StageCapability value) =>
        Stage != StageCapability.None
        && value.HasFlag(Stage);
}

internal sealed record ConditionalRuleCodeVocabularyEntry(
    ConditionalRuleCodeId Id,
    string Code);

internal sealed record AuthoringFeatureVocabularyEntry(
    AuthoringFeature Feature,
    string AuthoringKey,
    string DisplayLabel,
    IReadOnlyList<CapabilityVocabularyEntry> Capabilities,
    ConditionalRuleFeature? ConditionalRuleFeature = null,
    bool CanIgnoreWhenUnsupported = true,
    bool RequiresEveryCapability = true);

/// <summary>
/// The single cross-language vocabulary for architecture capabilities and authored features.
/// Capability enums remain the typed programming surface; this registry owns their wire names,
/// authoring keys, labels, and the relationships between them.
/// </summary>
internal static class ArchitectureFeatureVocabulary
{
    internal static IReadOnlyList<CapabilityVocabularyEntry> Capabilities { get; } =
    [
        For(ArchitectureCapability.GeneratedEntry, "generated-entry"),
        For(ArchitectureCapability.SourcedEntry, "sourced-entry"),
        For(ArchitectureCapability.MultiStage, "multi-stage"),
        For(ArchitectureCapability.NativeAudio, "native-audio"),
        For(ClipCapability.SourceVideo, "source-video"),
        For(ClipCapability.Prompts, "prompts"),
        For(ClipCapability.PromptRelay, "prompt-relay"),
        For(ClipCapability.References, "references"),
        For(ClipCapability.ReferenceFraming, "reference-framing"),
        For(ClipCapability.Retake, "retake"),
        For(ClipCapability.AudioSources, "audio-sources"),
        For(ClipCapability.AudioSegments, "audio-segments"),
        For(ClipCapability.AudioReuse, "audio-reuse"),
        For(ClipCapability.AudioDerivedDuration, "audio-derived-duration"),
        For(
            ClipCapability.ControlSignalDerivedDuration,
            "control-signal-derived-duration"),
        For(StageCapability.ImageInput, "image-input"),
        For(StageCapability.VideoInput, "video-input"),
        For(StageCapability.PixelUpscale, "pixel-upscale", "pixel"),
        For(StageCapability.ModelUpscale, "model-upscale", "model"),
        For(StageCapability.LatentUpscale, "latent-upscale", "latent"),
        For(
            StageCapability.LatentModelUpscale,
            "latent-model-upscale",
            "latent-model"),
        For(StageCapability.Lora, "lora"),
        For(StageCapability.IcLora, "ic-lora"),
        For(StageCapability.Hdr, "hdr"),
        For(StageCapability.FrameReferences, "frame-references"),
    ];

    internal static IReadOnlyList<AuthoringFeatureVocabularyEntry>
        AuthoringFeatures
    { get; } =
    [
        new(
            AuthoringFeature.MultiStage,
            "multiStage",
            "Multiple stages",
            [Capability(ArchitectureCapability.MultiStage)],
            CanIgnoreWhenUnsupported: false),
        new(
            AuthoringFeature.SourceVideo,
            "sourceVideo",
            "Source video",
            [Capability(ClipCapability.SourceVideo)],
            CanIgnoreWhenUnsupported: false),
        new(
            AuthoringFeature.FrameReferences,
            "frameReferences",
            "Frame references",
            [
                Capability(ClipCapability.References),
                Capability(StageCapability.FrameReferences),
            ],
            ConditionalRuleFeature.FrameReferences),
        new(
            AuthoringFeature.ReferenceFraming,
            "referenceFraming",
            "Reference framing",
            [Capability(ClipCapability.ReferenceFraming)]),
        new(
            AuthoringFeature.Retake,
            "retake",
            "Retakes",
            [Capability(ClipCapability.Retake)],
            ConditionalRuleFeature.Retake),
        new(
            AuthoringFeature.MajorPrompt,
            "majorPrompt",
            "Major prompts",
            [Capability(ClipCapability.Prompts)],
            CanIgnoreWhenUnsupported: false),
        new(
            AuthoringFeature.PromptRelay,
            "promptRelay",
            "Relay prompts",
            [Capability(ClipCapability.PromptRelay)]),
        new(
            AuthoringFeature.ClipAudio,
            "clipAudio",
            "Clip audio",
            [Capability(ClipCapability.AudioSources)]),
        new(
            AuthoringFeature.AudioReuse,
            "audioReuse",
            "Captured stage audio reuse",
            [Capability(ClipCapability.AudioReuse)]),
        new(
            AuthoringFeature.AudioDerivedDuration,
            "audioDerivedDuration",
            "Audio-derived clip duration",
            [Capability(ClipCapability.AudioDerivedDuration)]),
        new(
            AuthoringFeature.ControlSignalDerivedDuration,
            "controlSignalDerivedDuration",
            "Control-signal-derived clip duration",
            [Capability(ClipCapability.ControlSignalDerivedDuration)]),
        new(
            AuthoringFeature.StageLoras,
            "stageLoras",
            "LoRAs",
            [Capability(StageCapability.Lora)]),
        new(
            AuthoringFeature.IcLora,
            "icLora",
            "IC-LoRA",
            [Capability(StageCapability.IcLora)]),
        new(
            AuthoringFeature.Hdr,
            "hdr",
            "HDR",
            [Capability(StageCapability.Hdr)],
            ConditionalRuleFeature.Hdr),
        new(
            AuthoringFeature.Upscale,
            "upscale",
            "Stage upscaling",
            [
                Capability(StageCapability.PixelUpscale),
                Capability(StageCapability.ModelUpscale),
                Capability(StageCapability.LatentUpscale),
                Capability(StageCapability.LatentModelUpscale),
            ],
            RequiresEveryCapability: false),
    ];

    internal static IReadOnlyList<ConditionalRuleCodeVocabularyEntry>
        ConditionalRuleCodes
    { get; } =
    [
        new(
            ConditionalRuleCodeId.AudioReuseRequiresStages,
            "audio.reuse.requires_three_stages"),
        new(
            ConditionalRuleCodeId.NormalLoraRequiresSamplingStage,
            "normal-lora-requires-sampling-stage"),
        new(
            ConditionalRuleCodeId.PromptRelayRequiresFixedLength,
            "prompt-relay-dynamic-length-unsupported"),
        new(
            ConditionalRuleCodeId.RetakeExcludesReferences,
            "retake-frame-references-unsupported"),
        new(ConditionalRuleCodeId.RetakeRequiresSource, "retake-source-required"),
        new(ConditionalRuleCodeId.UniformTimelineHdr, "mixed-hdr-timeline-unsupported"),
    ];

    internal static string RuleCode(ConditionalRuleCodeId id) =>
        ConditionalRuleCodes.Single(entry => entry.Id == id).Code;

    internal static IEnumerable<string> WireNames(ArchitectureCapability value) =>
        Capabilities.Where(entry => entry.Supports(value)).Select(entry => entry.WireName);

    internal static IEnumerable<string> WireNames(ClipCapability value) =>
        Capabilities.Where(entry => entry.Supports(value)).Select(entry => entry.WireName);

    internal static IEnumerable<string> WireNames(StageCapability value) =>
        Capabilities.Where(entry => entry.Supports(value)).Select(entry => entry.WireName);

    internal static IEnumerable<string> UpscaleModeWireNames(StageCapability value) =>
        Capabilities
            .Where(entry => entry.Supports(value) && entry.UpscaleModeWireName is not null)
            .Select(entry => entry.UpscaleModeWireName);

    internal static string AuthoringKey(AuthoringFeature feature) =>
        AuthoringFeatures.Single(entry => entry.Feature == feature).AuthoringKey;

    internal static string AuthoringKey(ConditionalRuleFeature feature) =>
        AuthoringFeatures
            .Single(entry => entry.ConditionalRuleFeature == feature)
            .AuthoringKey;

    internal static bool Supports(
        ArchitectureCapabilityDescriptor capabilities,
        AuthoringFeature feature)
    {
        AuthoringFeatureVocabularyEntry entry =
            AuthoringFeatures.Single(candidate => candidate.Feature == feature);
        bool SupportsCapability(CapabilityVocabularyEntry capability) =>
            capability.Scope switch
            {
                CapabilityVocabularyScope.Architecture =>
                    capability.Supports(capabilities.Architecture),
                CapabilityVocabularyScope.Clip =>
                    capability.Supports(capabilities.Clip),
                CapabilityVocabularyScope.Stage =>
                    capability.Supports(capabilities.Stage),
                _ => throw new ArgumentOutOfRangeException(),
            };
        return entry.RequiresEveryCapability
            ? entry.Capabilities.All(SupportsCapability)
            : entry.Capabilities.Any(SupportsCapability);
    }

    internal static IReadOnlySet<AuthoringFeature>
        IgnoredWhenUnsupported(
            ArchitectureCapabilityDescriptor capabilities) =>
        AuthoringFeatures
            .Where(entry =>
                entry.CanIgnoreWhenUnsupported
                && !Supports(capabilities, entry.Feature))
            .Select(entry => entry.Feature)
            .ToHashSet();

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
        Line("export const CAPABILITY_WIRE_NAMES = {");
        foreach (CapabilityVocabularyScope scope in Enum.GetValues<CapabilityVocabularyScope>())
        {
            Line($"    {CamelCase(scope.ToString())}: {{");
            foreach (CapabilityVocabularyEntry capability in
                Capabilities.Where(entry => entry.Scope == scope))
            {
                Line(
                    $"        {CamelCase(capability.SymbolName)}: "
                    + $"{Quote(capability.WireName)},");
            }
            Line("    },");
        }
        Line("} as const;");
        Line();
        Line("export type CapabilityVocabularyScope = keyof typeof CAPABILITY_WIRE_NAMES;");
        Line();
        Line("export type GeneratedAuthoringFeatureCapability = readonly [");
        Line("    scope: CapabilityVocabularyScope,");
        Line("    wireName: string,");
        Line("    upscaleMode: string | null,");
        Line("];");
        Line();
        Line("export const AUTHORING_FEATURES = [");
        foreach (AuthoringFeatureVocabularyEntry feature in AuthoringFeatures)
        {
            Line($"    {Quote(feature.AuthoringKey)},");
        }
        Line("] as const;");
        Line();
        Line("export type GeneratedAuthoringFeature = (typeof AUTHORING_FEATURES)[number];");
        Line();
        Line("export const IGNORED_WHEN_UNSUPPORTED_FEATURES = [");
        foreach (AuthoringFeatureVocabularyEntry feature in
            AuthoringFeatures.Where(entry => entry.CanIgnoreWhenUnsupported))
        {
            Line($"    {Quote(feature.AuthoringKey)},");
        }
        Line("] as const satisfies readonly GeneratedAuthoringFeature[];");
        Line();
        Line("export const AUTHORING_FEATURES_REQUIRING_EVERY_CAPABILITY = [");
        foreach (AuthoringFeatureVocabularyEntry feature in
            AuthoringFeatures.Where(entry =>
                entry.Capabilities.Count > 1
                && entry.RequiresEveryCapability))
        {
            Line($"    {Quote(feature.AuthoringKey)},");
        }
        Line("] as const satisfies readonly GeneratedAuthoringFeature[];");
        Line();
        Line("export const doesAuthoringFeatureRequireEveryCapability = (");
        Line("    feature: GeneratedAuthoringFeature,");
        Line("): boolean =>");
        Line("    (");
        Line("        AUTHORING_FEATURES_REQUIRING_EVERY_CAPABILITY as readonly string[]");
        Line("    ).includes(feature);");
        Line();
        Line("export const isIgnoredWhenUnsupportedFeature = (");
        Line("    feature: GeneratedAuthoringFeature,");
        Line("): boolean =>");
        Line("    (IGNORED_WHEN_UNSUPPORTED_FEATURES as readonly string[]).includes(feature);");
        Line();
        Line("export const AUTHORING_FEATURE_LABELS: Record<");
        Line("    GeneratedAuthoringFeature,");
        Line("    string");
        Line("> = {");
        foreach (AuthoringFeatureVocabularyEntry feature in AuthoringFeatures)
        {
            Line($"    {feature.AuthoringKey}: {Quote(feature.DisplayLabel)},");
        }
        Line("};");
        Line();
        Line("export const AUTHORING_FEATURE_CAPABILITIES: Record<");
        Line("    GeneratedAuthoringFeature,");
        Line("    readonly GeneratedAuthoringFeatureCapability[]");
        Line("> = {");
        foreach (AuthoringFeatureVocabularyEntry feature in AuthoringFeatures)
        {
            if (feature.Capabilities.Count == 1)
            {
                CapabilityVocabularyEntry only = feature.Capabilities[0];
                string onlyScope = CamelCase(only.Scope.ToString());
                string onlyReference =
                    $"CAPABILITY_WIRE_NAMES.{onlyScope}.{CamelCase(only.SymbolName)}";
                string onlyUpscaleMode = only.UpscaleModeWireName is null
                    ? "null"
                    : Quote(only.UpscaleModeWireName);
                string inline =
                    $"    {feature.AuthoringKey}: "
                    + $"[[{Quote(onlyScope)}, {onlyReference}, {onlyUpscaleMode}]],";
                if (inline.Length <= 80)
                {
                    Line(inline);
                    continue;
                }
            }
            Line($"    {feature.AuthoringKey}: [");
            foreach (CapabilityVocabularyEntry capability in feature.Capabilities)
            {
                string scope = CamelCase(capability.Scope.ToString());
                string symbol = CamelCase(capability.SymbolName);
                string capabilityReference =
                    $"CAPABILITY_WIRE_NAMES.{scope}.{symbol}";
                string upscaleMode = capability.UpscaleModeWireName is null
                    ? "null"
                    : Quote(capability.UpscaleModeWireName);
                AppendBinding(scope, capabilityReference, upscaleMode);
            }
            Line("    ],");
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

        void AppendBinding(string scope, string capabilityReference, string upscaleMode)
        {
            string binding =
                $"        [{Quote(scope)}, {capabilityReference}, {upscaleMode}],";
            if (binding.Length <= 80)
            {
                Line(binding);
                return;
            }
            Line("        [");
            Line($"            {Quote(scope)},");
            Line($"            {capabilityReference},");
            Line($"            {upscaleMode},");
            Line("        ],");
        }
    }

    private static CapabilityVocabularyEntry For(
        ArchitectureCapability capability,
        string wireName) =>
        new(capability, ClipCapability.None, StageCapability.None, wireName);

    private static CapabilityVocabularyEntry For(
        ClipCapability capability,
        string wireName) =>
        new(ArchitectureCapability.None, capability, StageCapability.None, wireName);

    private static CapabilityVocabularyEntry For(
        StageCapability capability,
        string wireName,
        string upscaleModeWireName = null) =>
        new(
            ArchitectureCapability.None,
            ClipCapability.None,
            capability,
            wireName,
            upscaleModeWireName);

    private static CapabilityVocabularyEntry Capability(
        ArchitectureCapability capability) =>
        Capabilities.Single(entry => entry.Architecture == capability);

    private static CapabilityVocabularyEntry Capability(ClipCapability capability) =>
        Capabilities.Single(entry => entry.Clip == capability);

    private static CapabilityVocabularyEntry Capability(StageCapability capability) =>
        Capabilities.Single(entry => entry.Stage == capability);

    private static string CamelCase(string value) =>
        char.ToLowerInvariant(value[0]) + value[1..];

    private static string Quote(string value) =>
        $"\"{value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)}\"";
}
