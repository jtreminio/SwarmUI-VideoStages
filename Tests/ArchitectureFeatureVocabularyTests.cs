using System.Runtime.CompilerServices;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using Xunit;

namespace VideoStages.Tests;

public class ArchitectureFeatureVocabularyTests
{
    [Fact]
    public void Vocabulary_covers_every_typed_feature()
    {
        Assert.Equal(
            Enum.GetValues<ArchitectureFeature>()
                .Where(value => value != ArchitectureFeature.None)
                .OrderBy(value => value),
            ArchitectureFeatureVocabulary.Features
                .Select(entry => entry.Feature)
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
    public void Vocabulary_entries_are_unique()
    {
        int count = ArchitectureFeatureVocabulary.Features.Count;
        Assert.Equal(
            count,
            ArchitectureFeatureVocabulary.Features
                .Select(entry => entry.WireName)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            count,
            ArchitectureFeatureVocabulary.Features
                .Select(entry => entry.AuthoringKey)
                .Distinct(StringComparer.Ordinal)
                .Count());
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
