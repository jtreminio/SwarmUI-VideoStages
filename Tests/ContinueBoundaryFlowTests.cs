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
    // Each clip's Duration (0.6s at 24 fps) aligns to 17 authored frames. The native first clip
    // renders 16 frames; the target generates 17 authored frames plus an 8-frame hidden handle.
    private const int ContinueClipFrames = 16;
    private const int ContinueTargetFrames = 25;
    private const int ContinueWindowFrames = Ltx2BoundaryPolicy.DefaultFrames + 1;

    private static string TwoClipContinueStagesJson(TestModelBundle models, JObject secondClip = null)
    {
        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        firstClip["boundaryOut"] = "continue";

        secondClip ??= MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        // The continue-window resolution reads spec frame counts, which come from Duration (0.6s at the
        // harness's 24 fps aligns to 17); without one the window degrades to the conservative 1 frame.
        firstClip["duration"] = 0.6;
        secondClip["duration"] ??= 0.6;

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
        Assert.Contains(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>(),
            node => node.Length.LiteralAsInt() == ContinueTargetFrames);
        EmptyAudioNode handleSilence = Assert.Single(
            bridge.Graph.NodesOfType<EmptyAudioNode>(),
            node => node.Duration.LiteralAsDouble() ==
                Ltx2BoundaryPolicy.DefaultFrames / 24d);
        Assert.Contains(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            node => ReferenceEquals(node.Audio1.Connection, handleSilence.AUDIO));
        Assert.Equal(
            ContinueClipFrames + ContinueTargetFrames - ContinueWindowFrames,
            g.CurrentMedia.Frames);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Continue_boundary_shifts_start_refs_but_keeps_end_refs_relative_to_the_end()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject stage = MakeStage(
            models.VideoModel.Name,
            "Generated",
            control: 0.5,
            steps: 10);
        JObject secondClip = MakeClipWithRefs(
            [MakeRef("Base", frame: 2), MakeRef("Base", frame: 2, fromEnd: true)],
            stage);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            TwoClipContinueStagesJson(models, secondClip));
        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        int[] frameIndexes = bridge.Graph.NodesOfType<LTXVAddGuideNode>()
            .Select(node => node.FrameIdx.LiteralAsInt()!.Value)
            .Order()
            .ToArray();
        Assert.Equal([-2, 10], frameIndexes);
    }

    [Theory]
    [InlineData(Constants.BoundaryOutContinue, ContinueWindowFrames)]
    [InlineData(Constants.BoundaryOutCrossfade, Ltx2BoundaryPolicy.DefaultFrames)]
    public void Boundary_audio_carry_conditions_next_clip_sampler_from_previous_tail(
        string boundary,
        int carryFrames)
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        firstClip["boundaryOut"] = boundary;
        firstClip["boundaryOutCarryAudio"] = true;
        firstClip["duration"] = 0.6;
        JObject secondClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        secondClip["duration"] = 0.6;

        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(512, 512, firstClip, secondClip).ToString());
        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        LTXVConcatAVLatentNode secondStageInput = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>(),
            node => ReferenceEquals(node.Latent, samplers[1].LatentImage.Connection));
        SwarmSetAudioMaskWindowsNode carryMask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        Assert.Same(carryMask.Latent, secondStageInput.AudioLatent.Connection);
        Assert.Equal(1.0, carryMask.GapMaskValue.LiteralAsDouble());

        JArray windows = JArray.Parse(carryMask.Windows.LiteralAsString());
        JObject carryWindow = Assert.IsType<JObject>(Assert.Single(windows));
        Assert.Equal(0.0, carryWindow.Value<double>("start"), 6);
        Assert.Equal(
            Math.Round(carryFrames / 24.0, 2, MidpointRounding.AwayFromZero),
            carryWindow.Value<double>("end"),
            6);

        Assert.IsType<LTXVSeparateAVLatentNode>(
            carryMask.Samples.Connection!.Node);
        Assert.IsType<LTXVEmptyLatentAudioNode>(
            carryMask.TargetSamples.Connection!.Node);
        Assert.Equal(
            (ContinueClipFrames - carryFrames) / 24.0,
            carryMask.SourceStartSeconds.LiteralAsDouble()!.Value,
            6);
        Assert.True(ReachesUpstream(
            bridge,
            carryMask.Samples.Connection.Node,
            samplers[0].Id));
        Assert.True(ReachesUpstream(bridge, samplers[1], carryMask.Id));
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());

        // Carry is generation-time context. There is no second AudioMerge near final publication.
        Assert.Empty(bridge.Graph.NodesOfType<AudioMergeNode>());
        SwarmSaveAnimationWSNode save =
            Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.IsType<AudioConcatNode>(save.Audio.Connection!.Node);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Boundary_audio_carry_uses_plan_timing_when_next_clip_starts_from_image_root()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        firstClip["boundaryOut"] = Constants.BoundaryOutContinue;
        firstClip["boundaryOutCarryAudio"] = true;
        firstClip["duration"] = 5.0;
        JObject secondClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        secondClip["duration"] = 5.0;

        T2IParamInput input = BuildInput(
            models.BaseModel,
            MakeRootConfig(512, 512, firstClip, secondClip).ToString());
        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNoopSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmSetAudioMaskWindowsNode carryMask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        LTXVConcatAVLatentNode conditionedInput = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>(),
            node => ReferenceEquals(node.AudioLatent.Connection, carryMask.Latent));
        Assert.Contains(
            bridge.Graph.FindInputsConnectedTo(conditionedInput.Latent),
            consumer => consumer.Node is SwarmKSamplerNode
                && consumer.Input.Name == "latent_image");

        Assert.IsType<LTXVSeparateAVLatentNode>(
            carryMask.Samples.Connection!.Node);
        Assert.IsType<LTXVEmptyLatentAudioNode>(
            carryMask.TargetSamples.Connection!.Node);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
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
