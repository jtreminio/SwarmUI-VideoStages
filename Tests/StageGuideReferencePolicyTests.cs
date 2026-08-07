using VideoStages.Architectures.Abstractions;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageGuideReferencePolicyTests
{
    [Theory]
    [InlineData("Generated", true)]
    [InlineData("Base", false)]
    [InlineData("Stage12", false)]
    [InlineData("not-a-reference", false)]
    public void Generated_only_is_fail_closed(string rawValue, bool expected) =>
        Assert.Equal(
            expected,
            StageGuideReferencePolicy.GeneratedOnly.Allows(
                StageGuideReference.Classify(rawValue)));

    [Fact]
    public void Undefined_selector_flags_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StageGuideReferencePolicy((StageGuideReferenceKind)(1 << 20)));
}
