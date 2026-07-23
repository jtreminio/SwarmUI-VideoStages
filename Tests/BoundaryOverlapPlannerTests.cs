using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
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
        int continuityWindow = 0) =>
        new(
            from,
            mode,
            mode == BoundaryExecutionMode.Cut ? 0 : overlap,
            mode == BoundaryExecutionMode.Continue ? continuityWindow : 0,
            RequiresRuntimeMergeValidation: mode != BoundaryExecutionMode.Cut,
            BoundaryFallback.None);

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
        Assert.Equal(7, resolution.Boundaries[1].OverlapFrames);
    }

    [Fact]
    public void ResolvePlanBudgets_ShrinksShortContinueOnceForAllConsumers()
    {
        BoundaryBudgetResolution resolution = BoundaryOverlapPlanner.ResolvePlanBudgets(
            [5, 5],
            [Boundary(0, BoundaryExecutionMode.Continue, continuityWindow: 9)]);
        BoundaryPlan boundary = Assert.Single(resolution.Boundaries);

        Assert.Equal(BoundaryExecutionMode.Continue, boundary.Effective);
        Assert.Equal(1, boundary.ContinuityWindowFrames);
        Assert.Equal(0, boundary.OverlapFrames);
        Assert.True(resolution.Degraded);
        Assert.Equal([1], BoundaryOverlapPlanner.ToOverlapPlan(resolution.Boundaries).BoundaryOverlap);
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
