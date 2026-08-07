using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageGuideReferenceTests
{
    [Theory]
    [InlineData("Generated", (int)StageGuideReferenceKind.Generated, -1)]
    [InlineData("Base", (int)StageGuideReferenceKind.Base, -1)]
    [InlineData("Refiner", (int)StageGuideReferenceKind.Refiner, -1)]
    [InlineData("PreviousStage", (int)StageGuideReferenceKind.PreviousStage, -1)]
    [InlineData("Stage12", (int)StageGuideReferenceKind.ExplicitStage, 12)]
    [InlineData("edit7", (int)StageGuideReferenceKind.Base2Edit, 7)]
    [InlineData("not-a-reference", (int)StageGuideReferenceKind.Unknown, -1)]
    public void Classification_is_finite(
        string rawValue,
        int expectedKindValue,
        int expectedIndex)
    {
        StageGuideReferenceSelection selection = StageGuideReference.Classify(rawValue);

        Assert.Equal((StageGuideReferenceKind)expectedKindValue, selection.Kind);
        Assert.Equal(expectedIndex < 0 ? null : expectedIndex, selection.ReferencedStageIndex);
    }

    /// <summary>Only an index-carrying kind may carry an index — the reader's switch reads it
    /// without a null check.</summary>
    [Theory]
    [InlineData("Stage12")]
    [InlineData("edit7")]
    public void Only_indexed_kinds_carry_an_index(string rawValue) =>
        Assert.NotNull(StageGuideReference.Classify(rawValue).ReferencedStageIndex);
}
