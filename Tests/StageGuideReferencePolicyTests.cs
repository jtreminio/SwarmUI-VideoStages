using VideoStages.Architectures.Abstractions;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageGuideReferencePolicyTests
{
    [Theory]
    [InlineData("Generated", (int)StageGuideReferenceKind.Generated, -1)]
    [InlineData("Base", (int)StageGuideReferenceKind.Base, -1)]
    [InlineData("Refiner", (int)StageGuideReferenceKind.Refiner, -1)]
    [InlineData("PreviousStage", (int)StageGuideReferenceKind.PreviousStage, -1)]
    [InlineData("Stage12", (int)StageGuideReferenceKind.ExplicitStage, 12)]
    [InlineData("edit7", (int)StageGuideReferenceKind.Base2Edit, 7)]
    [InlineData("not-a-reference", (int)StageGuideReferenceKind.Unknown, -1)]
    public void Stage_guide_reference_classification_is_finite_and_generated_only_is_fail_closed(
        string rawValue,
        int expectedKindValue,
        int expectedIndex)
    {
        StageGuideReferenceSelection selection =
            StageGuideReferencePolicy.Classify(rawValue);
        StageGuideReferenceKind expectedKind =
            (StageGuideReferenceKind)expectedKindValue;

        Assert.Equal(expectedKind, selection.Kind);
        Assert.Equal(expectedIndex < 0 ? null : expectedIndex, selection.ReferencedStageIndex);
        Assert.Equal(
            expectedKind == StageGuideReferenceKind.Generated,
            StageGuideReferencePolicy.GeneratedOnly.Allows(selection));
    }

    [Fact]
    public void Stage_guide_reference_policy_rejects_undefined_selector_flags()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StageGuideReferencePolicy((StageGuideReferenceKind)(1 << 20)));
    }
}
