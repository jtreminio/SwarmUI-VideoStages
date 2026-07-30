using System.Runtime.CompilerServices;
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
            Enum.GetValues<UnsupportedAuthoringFeature>().OrderBy(value => value),
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
