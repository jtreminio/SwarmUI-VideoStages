using VideoStages.Authoring;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxReferenceImageSizeTests
{
    [Fact]
    public void Generated_reference_image_size_options_are_current()
    {
        Assert.Equal(
            MiniMaxReferenceImageSizes.RenderGeneratedTypeScript(),
            RepoFiles.ReadFrontend("generatedMiniMaxReferenceImageSize.ts"));
    }

    [Theory]
    [InlineData(null, MiniMaxReferenceImageSize.Match)]
    [InlineData("unknown", MiniMaxReferenceImageSize.Match)]
    [InlineData(" MAX ", MiniMaxReferenceImageSize.Max)]
    public void Reference_image_size_parsing_falls_back_to_match(
        string value,
        MiniMaxReferenceImageSize expected)
    {
        Assert.Equal(expected, MiniMaxReferenceImageSizes.Parse(value));
    }
}
