using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ImageReferenceSyntaxTests
{
    [Theory]
    [InlineData("Stage0", 0)]
    [InlineData(" stage 12 ", 12)]
    [InlineData("STAGE 2", 2)]
    [InlineData("Stage00", 0)]
    public void Explicit_stage_reference_parses_expected_index(string rawValue, int expectedIndex)
    {
        Assert.True(ImageReferenceSyntax.TryParseExplicitStageIndex(rawValue, out int actualIndex));
        Assert.Equal(expectedIndex, actualIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Stage")]
    [InlineData("Stage-1")]
    [InlineData("Stageabc")]
    [InlineData("Staging")]
    public void Invalid_stage_reference_is_rejected(string rawValue)
    {
        Assert.False(ImageReferenceSyntax.TryParseExplicitStageIndex(rawValue, out int actualIndex));
        Assert.Equal(-1, actualIndex);
    }
}
