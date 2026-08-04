using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class BoundaryOverlapPlannerTests
{
    private static BoundaryPlan Boundary(
        int from,
        BoundaryJoinType mode,
        int overlap = 8,
        int continuityWindow = 0,
        int frameStep = 8,
        int minFrames = 8) =>
        new(
            from,
            mode,
            mode == BoundaryJoinType.Cut ? 0 : overlap,
            mode == BoundaryJoinType.Continue ? continuityWindow : 0,
            BoundaryFallbackReason.None)
        {
            FrameStep = frameStep,
            MinFrames = mode == BoundaryJoinType.Cut ? 0 : minFrames,
        };

    [Fact]
    public void FitPlanToFrameBudgets_UsesTypedModesAndReservesNeighborBudget()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.FitPlanToFrameBudgets(
            [17, 17, 17],
            [
                Boundary(0, BoundaryJoinType.Continue, continuityWindow: 9),
                Boundary(1, BoundaryJoinType.Crossfade),
            ]);

        Assert.Equal(9, resolution.Boundaries[0].ContinuityWindowFrames);
        Assert.Equal(BoundaryJoinType.Crossfade, resolution.Boundaries[1].Effective);
        Assert.Equal(8, resolution.Boundaries[1].OverlapFrames);
    }

    [Fact]
    public void FitPlanToFrameBudgets_CutsWhenShortContinueCannotPreserveArchitectureMinimum()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.FitPlanToFrameBudgets(
            [5, 5],
            [Boundary(0, BoundaryJoinType.Continue, continuityWindow: 9)]);
        BoundaryPlan boundary = Assert.Single(resolution.Boundaries);

        Assert.Equal(BoundaryJoinType.Cut, boundary.Effective);
        Assert.Equal(0, boundary.ContinuityWindowFrames);
        Assert.Equal(0, boundary.OverlapFrames);
        Assert.True(resolution.Degraded);
        Assert.Null(BoundaryOverlapPlanner.ToOverlapPlan(resolution.Boundaries));
    }

    [Fact]
    public void FitPlanToFrameBudgets_ReducesOnlyOnTheArchitectureMinimumRelativeGrid()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.FitPlanToFrameBudgets(
            [10, 10],
            [
                Boundary(
                    0,
                    BoundaryJoinType.Crossfade,
                    overlap: 13,
                    frameStep: 4,
                    minFrames: 5),
            ]);
        BoundaryPlan boundary = Assert.Single(resolution.Boundaries);

        Assert.Equal(BoundaryJoinType.Crossfade, boundary.Effective);
        Assert.Equal(9, boundary.OverlapFrames);
        Assert.Equal(0, (boundary.OverlapFrames - boundary.MinFrames) % boundary.FrameStep);
    }

    [Fact]
    public void FitPlanToFrameBudgets_LeavesUnknownLengthsProvisional()
    {
        BoundaryPlan boundary = Boundary(
            0,
            BoundaryJoinType.Crossfade,
            overlap: 13,
            frameStep: 4,
            minFrames: 5);

        BoundaryBudgetResolution resolution =
            BoundaryOverlapPlanner.FitPlanToFrameBudgets([null, 10], [boundary]);

        Assert.False(resolution.Degraded);
        Assert.Equal(13, Assert.Single(resolution.Boundaries).OverlapFrames);
    }

    [Fact]
    public void BoundaryPolicy_NormalizesRelativeToItsMinimumInsteadOfZero()
    {
        RuleDecision policy = RuleDecision.Conditional(
            "fake.crossfade",
            "test",
            new BoundaryRuleConstraints(
                FrameStep: 4,
                MinFrames: 5,
                MaxFrames: 21,
                DefaultFrames: 9,
                ContinuityExtraFrames: 0,
                TargetRequiresGeneratedEntry: false,
                TargetRequiresStage: false,
                TargetDisallowsInitialReference: false));

        Assert.Equal(9, BoundaryPlanCompiler.NormalizeOverlap(policy, 0));
        Assert.Equal(5, BoundaryPlanCompiler.NormalizeOverlap(policy, 4));
        Assert.Equal(5, BoundaryPlanCompiler.NormalizeOverlap(policy, 8));
        Assert.Equal(9, BoundaryPlanCompiler.NormalizeOverlap(policy, 10));
        Assert.Equal(21, BoundaryPlanCompiler.NormalizeOverlap(policy, 999));
    }

    [Fact]
    public void ValidateRuntime_ExplicitlyCutsWhenArtifactLengthsCannotHonorCompiledWindow()
    {
        BoundaryPlan compiled = Boundary(
            0,
            BoundaryJoinType.Continue,
            overlap: 8,
            continuityWindow: 9);
        BoundaryBudgetResolution resolution =
            BoundaryOverlapPlanner.ValidateRuntime([Clip(1, 5), Clip(2, 5)], [compiled]);

        Assert.True(resolution.Degraded);
        Assert.Equal(BoundaryJoinType.Cut, Assert.Single(resolution.Boundaries).Effective);
        Assert.Contains("runtime clip lengths", resolution.Reason);
    }

    [Fact]
    public void ValidateRuntime_CutsInsteadOfShrinkingOnThePolicyGrid()
    {
        BoundaryPlan compiled = Boundary(
            0,
            BoundaryJoinType.Crossfade,
            overlap: 13,
            frameStep: 4,
            minFrames: 5);

        BoundaryBudgetResolution resolution =
            BoundaryOverlapPlanner.ValidateRuntime([Clip(1, 10), Clip(2, 10)], [compiled]);

        Assert.True(resolution.Degraded);
        Assert.Equal(BoundaryJoinType.Cut, Assert.Single(resolution.Boundaries).Effective);
    }

    [Fact]
    public void ValidateRuntime_CutsOnlyTheFailingOverlapRun()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.ValidateRuntime(
            [Clip(1, 20), Clip(2, 20), Clip(3, 5), Clip(4, 5)],
            [
                Boundary(0, BoundaryJoinType.Crossfade),
                Boundary(1, BoundaryJoinType.Cut),
                Boundary(2, BoundaryJoinType.Crossfade),
            ]);

        Assert.Equal(BoundaryJoinType.Crossfade, resolution.Boundaries[0].Effective);
        Assert.Equal(BoundaryJoinType.Cut, resolution.Boundaries[1].Effective);
        Assert.Equal(BoundaryJoinType.Cut, resolution.Boundaries[2].Effective);
        Assert.Equal(
            BoundaryFallbackReason.InsufficientFrameBudget,
            resolution.Boundaries[2].Fallback);
        Assert.DoesNotContain("clips 0-1", resolution.Reason);
        Assert.Contains("clips 2-3", resolution.Reason);
    }

    private static DecodedClipArtifact Clip(int id, int frames) => new(
        new DecodedOutputHandle($"{id}", 0, DecodedMediaKind.Video),
        Audio: null,
        Width: 512,
        Height: 512,
        FramesPerSecond: 24,
        Frames: frames,
        new("test-arch"),
        id);
}
