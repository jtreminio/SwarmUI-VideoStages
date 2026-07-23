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
    // Each clip's Duration (0.6s at 24 fps) aligns to 17 spec frames, which funds the continue-window
    // resolution: the default overlap (8) resolves to a 9-frame window (overlap+1). The refine-type
    // stages themselves carry the native source's 16 frames through, so each RENDERED clip is 16 frames
    // — the window's tail slice and the merge trims index off that actual output count.
    private const int ContinueClipFrames = 16;
    private const int ContinueWindowFrames = Ltx2BoundaryPolicy.DefaultFrames + 1;

    private static string TwoClipContinueStagesJson(TestModelBundle models, JObject secondClip = null)
    {
        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        firstClip["BoundaryOut"] = "continue";

        secondClip ??= MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        // The continue-window resolution reads spec frame counts, which come from Duration (0.6s at the
        // harness's 24 fps aligns to 17); without one the window degrades to the conservative 1 frame.
        firstClip["Duration"] = 0.6;
        secondClip["Duration"] ??= 0.6;

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

        // The continuity tail: clip 0's last overlap+1 frames, sliced out as one batch. (The merger's
        // K=9 tail slice shares the same index/length, so there are two such nodes in the graph.)
        List<ImageFromBatchNode> tailSlices = [.. bridge.Graph.NodesOfType<ImageFromBatchNode>()
            .Where(n => n.BatchIndex.LiteralAsInt() == ContinueClipFrames - ContinueWindowFrames
                && n.Length.LiteralAsInt() == ContinueWindowFrames)];
        Assert.NotEmpty(tailSlices);

        // Clip 1's first stage freezes it as full-strength opening latent context (img-to-video inplace).
        List<LTXVImgToVideoInplaceNode> inplaceNodes =
            [.. bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()];
        Assert.Contains(inplaceNodes, n =>
            n.Strength.LiteralAsDouble() == 1.0
            && tailSlices.Any(slice => ReachesUpstream(bridge, n, slice.Id)));

        // The merge collapses the duplicated window: one K=9 blend fed a single 9-frame ramp mask node.
        LTXVLaplacianPyramidBlendNode blend = Assert.Single(
            bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(ContinueWindowFrames, ramp.Frames.LiteralAsInt());
        Assert.Equal(ramp.Id, blend.Mask.Connection!.Node.Id);
        Assert.Equal(2 * ContinueClipFrames - ContinueWindowFrames, g.CurrentMedia.Frames);
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
            n => n.BatchIndex.LiteralAsInt() == ContinueClipFrames - ContinueWindowFrames
                && n.Length.LiteralAsInt() == ContinueWindowFrames);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Equal(2 * ContinueClipFrames, g.CurrentMedia.Frames);
        AssertWorkflowHasNoCycles(workflow);
    }
}
