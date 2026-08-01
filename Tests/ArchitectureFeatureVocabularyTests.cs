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
    public void Vocabulary_entries_are_unique()
    {
        Assert.Equal(
            ArchitectureFeatureVocabulary.Features.Count,
            ArchitectureFeatureVocabulary.Features
                .Select(entry => entry.WireName)
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
