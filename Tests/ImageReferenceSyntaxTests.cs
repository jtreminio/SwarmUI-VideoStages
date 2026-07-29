using VideoStages.Architectures.Abstractions;
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
        Assert.True(ImageReference.TryParseExplicitStageIndex(rawValue, out int actualIndex));
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
        Assert.False(ImageReference.TryParseExplicitStageIndex(rawValue, out int actualIndex));
        Assert.Equal(-1, actualIndex);
    }

    [Theory]
    [InlineData("edit0", 0)]
    [InlineData(" edit 7 ", 7)]
    [InlineData("EDIT12", 12)]
    public void Base2Edit_stage_reference_parses_expected_index(string rawValue, int expectedIndex)
    {
        Assert.True(ImageReference.TryParseBase2EditStageIndex(rawValue, out int actualIndex));
        Assert.Equal(expectedIndex, actualIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("edit")]
    [InlineData("edit-1")]
    [InlineData("editabc")]
    [InlineData("Edit Stage 0")]
    public void Invalid_base2edit_stage_reference_is_rejected(string rawValue)
    {
        Assert.False(ImageReference.TryParseBase2EditStageIndex(rawValue, out int actualIndex));
        Assert.Equal(-1, actualIndex);
    }

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

    [Fact]
    public void Stage_guide_reference_policy_rejects_non_finite_selections()
    {
        StageGuideReferencePolicy policy = new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.Base);

        Assert.False(policy.Allows(new(
            StageGuideReferenceKind.Generated | StageGuideReferenceKind.Base,
            null)));
        Assert.False(policy.Allows(new(
            (StageGuideReferenceKind)(1 << 20),
            null)));
    }
}
