using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    // BuildNativeInput sets VideoFrames=16 and refine-type clip stages carry the source frame count
    // through, so each clip's rendered output is 16 frames; the continuity frame is batch index 15.
    private const int ContinueClipFrames = 16;

    private static string TwoClipContinueStagesJson(TestModelBundle models, JObject secondClip = null)
    {
        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        firstClip["BoundaryOut"] = "continue";

        secondClip ??= MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        return MakeRootConfig(512, 512, firstClip, secondClip).ToString();
    }

    [Fact]
    public void Continue_boundary_generates_next_clip_from_previous_last_frame_and_collapses_seam()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, TwoClipContinueStagesJson(models));
        (JObject workflow, WorkflowGenerator g) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The continuity frame: clip 0's final rendered frame, sliced out as a single image. (The merger's
        // K=1 tail slice shares the same index/length, so there are two such nodes in the graph.)
        List<ImageFromBatchNode> lastFrameSlices = [.. bridge.Graph.NodesOfType<ImageFromBatchNode>()
            .Where(n => n.BatchIndex.LiteralAsInt() == ContinueClipFrames - 1
                && n.Length.LiteralAsInt() == 1)];
        Assert.NotEmpty(lastFrameSlices);

        // Clip 1's first stage consumes it as a full-strength first-frame guide (img-to-video inplace).
        List<LTXVImgToVideoInplaceNode> inplaceNodes =
            [.. bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()];
        Assert.Contains(inplaceNodes, n =>
            n.Strength.LiteralAsDouble() == 1.0
            && lastFrameSlices.Any(slice => ReachesUpstream(bridge, n, slice.Id)));

        // The merge collapses the duplicated seam frame: one K=1 blend with a single 50/50 mask frame.
        LTXVLaplacianPyramidBlendNode blend = Assert.Single(
            bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        SolidMaskNode mask = Assert.Single(bridge.Graph.NodesOfType<SolidMaskNode>());
        Assert.Equal(0.5, mask.Value.LiteralAsDouble()!.Value, 6);
        Assert.NotNull(blend.Mask.Connection);
        Assert.Equal(2 * ContinueClipFrames - 1, g.CurrentMedia.Frames);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Continue_boundary_with_explicit_first_frame_ref_degrades_to_cut()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // The user pinned their own first-frame ref on clip 1, which overrides the incoming continuity.
        JObject secondClip = MakeClipWithRefs(
            [MakeRef("Base", frame: 1)],
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, TwoClipContinueStagesJson(models, secondClip));
        (JObject workflow, WorkflowGenerator g) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // No continuity slice, no seam blend — the boundary degraded to a plain cut.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            n => n.BatchIndex.LiteralAsInt() == ContinueClipFrames - 1 && n.Length.LiteralAsInt() == 1);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(2 * ContinueClipFrames, g.CurrentMedia.Frames);
        AssertWorkflowHasNoCycles(workflow);
    }
}
