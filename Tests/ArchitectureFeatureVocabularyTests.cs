using System.Runtime.CompilerServices;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using Xunit;

namespace VideoStages.Tests;

public class ArchitectureFeatureVocabularyTests
{
    [Fact]
    public void Vocabulary_covers_every_typed_capability_and_authoring_feature()
    {
        Assert.Equal(
            Enum.GetValues<ArchitectureCapability>()
                .Where(value => value != ArchitectureCapability.None)
                .OrderBy(value => value),
            ArchitectureFeatureVocabulary.Capabilities
                .Where(entry => entry.Architecture != ArchitectureCapability.None)
                .Select(entry => entry.Architecture)
                .OrderBy(value => value));
        Assert.Equal(
            Enum.GetValues<ClipCapability>()
                .Where(value => value != ClipCapability.None)
                .OrderBy(value => value),
            ArchitectureFeatureVocabulary.Capabilities
                .Where(entry => entry.Clip != ClipCapability.None)
                .Select(entry => entry.Clip)
                .OrderBy(value => value));
        Assert.Equal(
            Enum.GetValues<StageCapability>()
                .Where(value => value != StageCapability.None)
                .OrderBy(value => value),
            ArchitectureFeatureVocabulary.Capabilities
                .Where(entry => entry.Stage != StageCapability.None)
                .Select(entry => entry.Stage)
                .OrderBy(value => value));
        Assert.Equal(
            Enum.GetValues<AuthoringFeature>().OrderBy(value => value),
            ArchitectureFeatureVocabulary.AuthoringFeatures
                .Select(entry => entry.Feature)
                .OrderBy(value => value));
        Assert.Equal(
            Enum.GetValues<ConditionalRuleFeature>().OrderBy(value => value),
            ArchitectureFeatureVocabulary.AuthoringFeatures
                .Where(entry => entry.ConditionalRuleFeature is not null)
                .Select(entry => entry.ConditionalRuleFeature!.Value)
                .OrderBy(value => value));
    }

    [Fact]
    public void Every_published_conditional_rule_code_is_registered()
    {
        Assert.Equal(
            Enum.GetValues<ConditionalRuleCodeId>().OrderBy(value => value),
            ArchitectureFeatureVocabulary.ConditionalRuleCodes
                .Select(entry => entry.Id)
                .OrderBy(value => value));

        string[] registered =
        [
            .. ArchitectureFeatureVocabulary.ConditionalRuleCodes
                .Select(entry => entry.Code),
        ];
        string[] published =
        [
            .. VideoArchitectureManifest.ProductionModules
                .SelectMany(module => module.Descriptor.Rules)
                .Select(rule => rule.Code)
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.Empty(published.Except(registered, StringComparer.Ordinal));
        Assert.Empty(registered.Except(published, StringComparer.Ordinal));
    }

    [Fact]
    public void Vocabulary_entries_are_unambiguous_and_unique()
    {
        Assert.All(
            ArchitectureFeatureVocabulary.Capabilities,
            entry =>
            {
                int owners =
                    (entry.Architecture == ArchitectureCapability.None ? 0 : 1)
                    + (entry.Clip == ClipCapability.None ? 0 : 1)
                    + (entry.Stage == StageCapability.None ? 0 : 1);
                Assert.Equal(1, owners);
            });
        Assert.Equal(
            ArchitectureFeatureVocabulary.Capabilities.Count,
            ArchitectureFeatureVocabulary.Capabilities
                .Select(entry => (entry.Scope, entry.WireName))
                .Distinct()
                .Count());
        Assert.Equal(
            ArchitectureFeatureVocabulary.AuthoringFeatures.Count,
            ArchitectureFeatureVocabulary.AuthoringFeatures
                .Select(entry => entry.Feature)
                .Distinct()
                .Count());
        Assert.Equal(
            ArchitectureFeatureVocabulary.AuthoringFeatures.Count,
            ArchitectureFeatureVocabulary.AuthoringFeatures
                .Select(entry => entry.AuthoringKey)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(
            ArchitectureFeatureVocabulary.AuthoringFeatures,
            entry => Assert.NotEmpty(entry.Capabilities));
    }

    [Fact]
    public void Frame_references_require_both_clip_and_stage_capabilities()
    {
        ArchitectureCapabilityDescriptor clipOnly = new(
            ArchitectureCapability.None,
            ClipCapability.References,
            StageCapability.None);
        ArchitectureCapabilityDescriptor stageOnly = new(
            ArchitectureCapability.None,
            ClipCapability.None,
            StageCapability.FrameReferences);
        ArchitectureCapabilityDescriptor complete = new(
            ArchitectureCapability.None,
            ClipCapability.References,
            StageCapability.FrameReferences);

        Assert.False(ArchitectureFeatureVocabulary.Supports(
            clipOnly,
            AuthoringFeature.FrameReferences));
        Assert.False(ArchitectureFeatureVocabulary.Supports(
            stageOnly,
            AuthoringFeature.FrameReferences));
        Assert.True(ArchitectureFeatureVocabulary.Supports(
            complete,
            AuthoringFeature.FrameReferences));
    }

    [Fact]
    public void Upscale_support_requires_any_published_upscale_mode()
    {
        ArchitectureCapabilityDescriptor pixelOnly = new(
            ArchitectureCapability.None,
            ClipCapability.None,
            StageCapability.PixelUpscale);
        ArchitectureCapabilityDescriptor latentOnly = new(
            ArchitectureCapability.None,
            ClipCapability.None,
            StageCapability.LatentUpscale);
        ArchitectureCapabilityDescriptor none = new(
            ArchitectureCapability.None,
            ClipCapability.None,
            StageCapability.None);

        Assert.True(ArchitectureFeatureVocabulary.Supports(
            pixelOnly,
            AuthoringFeature.Upscale));
        Assert.True(ArchitectureFeatureVocabulary.Supports(
            latentOnly,
            AuthoringFeature.Upscale));
        Assert.False(ArchitectureFeatureVocabulary.Supports(
            none,
            AuthoringFeature.Upscale));
    }

    [Fact]
    public void Capability_binding_mode_is_the_generated_frontend_authority()
    {
        Assert.True(
            ArchitectureFeatureVocabulary.AuthoringFeatures
                .Single(entry =>
                    entry.Feature == AuthoringFeature.FrameReferences)
                .RequiresEveryCapability);
        Assert.False(
            ArchitectureFeatureVocabulary.AuthoringFeatures
                .Single(entry => entry.Feature == AuthoringFeature.Upscale)
                .RequiresEveryCapability);
    }

    [Fact]
    public void Structural_features_are_never_silently_ignored()
    {
        AuthoringFeature[] structural =
        [
            AuthoringFeature.MultiStage,
            AuthoringFeature.SourceVideo,
            AuthoringFeature.MajorPrompt,
        ];

        Assert.All(
            structural,
            feature => Assert.False(
                ArchitectureFeatureVocabulary.AuthoringFeatures
                    .Single(entry => entry.Feature == feature)
                    .CanIgnoreWhenUnsupported));
        Assert.DoesNotContain(
            ArchitectureFeatureVocabulary.IgnoredWhenUnsupported(
                new(
                    ArchitectureCapability.None,
                    ClipCapability.None,
                    StageCapability.None)),
            structural.Contains);
    }

    [Fact]
    public void Generated_typescript_feature_vocabulary_is_current()
    {
        string committedPath = Path.GetFullPath(
            Path.Combine(TestSourceDirectory(), "..", "frontend", "architectures",
                "generatedFeatures.ts"));
        string committed = File.ReadAllText(committedPath);

        Assert.Equal(
            ArchitectureFeatureVocabulary.RenderGeneratedTypeScript(),
            committed);
    }

    private static string TestSourceDirectory(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
