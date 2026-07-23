using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class BoundaryOverlapPlannerTests
{
    private static BoundaryPlan Boundary(
        int from,
        BoundaryExecutionMode mode,
        int overlap = 8,
        int continuityWindow = 0,
        int frameStep = 8,
        int minFrames = 8) =>
        new(
            from,
            mode,
            mode,
            mode == BoundaryExecutionMode.Cut ? 0 : overlap,
            mode == BoundaryExecutionMode.Continue ? continuityWindow : 0,
            RequiresRuntimeMergeValidation: mode != BoundaryExecutionMode.Cut,
            BoundaryFallback.None)
        {
            FrameStep = frameStep,
            MinFrames = mode == BoundaryExecutionMode.Cut ? 0 : minFrames,
        };

    [Fact]
    public void ResolvePlanBudgets_UsesTypedModesAndReservesNeighborBudget()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [17, 17, 17],
            [
                Boundary(0, BoundaryExecutionMode.Continue, continuityWindow: 9),
                Boundary(1, BoundaryExecutionMode.Crossfade),
            ]);

        Assert.Equal(9, resolution.Boundaries[0].ContinuityWindowFrames);
        Assert.Equal(BoundaryExecutionMode.Cut, resolution.Boundaries[1].Effective);
        Assert.Equal(0, resolution.Boundaries[1].OverlapFrames);
        Assert.Equal(
            BoundaryFallback.InsufficientFrameBudget,
            resolution.Boundaries[1].Fallback);
    }

    [Fact]
    public void ResolvePlanBudgets_CutsWhenShortContinueCannotPreserveArchitectureMinimum()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [5, 5],
            [Boundary(0, BoundaryExecutionMode.Continue, continuityWindow: 9)]);
        BoundaryPlan boundary = Assert.Single(resolution.Boundaries);

        Assert.Equal(BoundaryExecutionMode.Cut, boundary.Effective);
        Assert.Equal(0, boundary.ContinuityWindowFrames);
        Assert.Equal(0, boundary.OverlapFrames);
        Assert.True(resolution.Degraded);
        Assert.Null(BoundaryOverlapPlanner.ToOverlapPlan(resolution.Boundaries));
    }

    [Fact]
    public void ResolvePlanBudgets_ReducesOnlyOnTheArchitectureMinimumRelativeGrid()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [10, 10],
            [
                Boundary(
                    0,
                    BoundaryExecutionMode.Crossfade,
                    overlap: 13,
                    frameStep: 4,
                    minFrames: 5),
            ]);
        BoundaryPlan boundary = Assert.Single(resolution.Boundaries);

        Assert.Equal(BoundaryExecutionMode.Crossfade, boundary.Effective);
        Assert.Equal(9, boundary.OverlapFrames);
        Assert.Equal(0, (boundary.OverlapFrames - boundary.MinFrames) % boundary.FrameStep);
    }

    [Fact]
    public void BoundaryPolicy_NormalizesRelativeToItsMinimumInsteadOfZero()
    {
        ArchitectureBoundaryModePolicy policy = new(
            RuleSupport.Conditional,
            "fake.crossfade",
            "test",
            FrameStep: 4,
            MinFrames: 5,
            MaxFrames: 21,
            DefaultFrames: 9,
            ContinuityExtraFrames: 0,
            TargetRequiresGeneratedEntry: false,
            TargetRequiresStage: false,
            TargetDisallowsInitialReference: false);

        Assert.Equal(9, policy.NormalizeOverlap(0));
        Assert.Equal(5, policy.NormalizeOverlap(4));
        Assert.Equal(5, policy.NormalizeOverlap(8));
        Assert.Equal(9, policy.NormalizeOverlap(10));
        Assert.Equal(21, policy.NormalizeOverlap(999));
    }

    [Fact]
    public void ValidateRuntime_ExplicitlyCutsWhenArtifactLengthsCannotHonorCompiledWindow()
    {
        WorkflowGenerator generator = new()
        {
            Workflow = [],
            UserInput = new(null),
            Features = [],
        };
        BoundaryPlan compiled = Boundary(
            0,
            BoundaryExecutionMode.Continue,
            overlap: 8,
            continuityWindow: 9);
        WGNodeData Clip(int id) => new(
            new JArray($"{id}", 0),
            generator,
            WGNodeData.DT_VIDEO,
            T2IModelClassSorter.CompatLtxv2)
        {
            Width = 512,
            Height = 512,
            Frames = 5,
            FPS = new JValue(24),
        };

        BoundaryBudgetResolution resolution =
            BoundaryOverlapPlanner.ValidateRuntime([Clip(1), Clip(2)], [compiled]);

        Assert.True(resolution.Degraded);
        Assert.Equal(BoundaryExecutionMode.Cut, Assert.Single(resolution.Boundaries).Effective);
        Assert.Contains("runtime clip lengths", resolution.Reason);
    }
}
