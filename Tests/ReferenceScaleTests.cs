using System.Runtime.CompilerServices;
using VideoStages.Authoring;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ReferenceScaleTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.25, 0.25)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(0.75, 1)]
    [InlineData(2, 1)]
    public void Normalize_keeps_the_offered_factors_and_falls_back_to_full(
        double authored,
        double expected) =>
        Assert.Equal(expected, ReferenceScale.Normalize(authored));

    [Fact]
    public void Generated_typescript_reference_scales_are_current()
    {
        string committedPath = Path.GetFullPath(
            Path.Combine(TestSourceDirectory(), "..", "frontend", "generatedReferenceScale.ts"));

        Assert.Equal(
            ReferenceScale.RenderGeneratedTypeScript(),
            File.ReadAllText(committedPath));
    }

    private static string TestSourceDirectory(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
