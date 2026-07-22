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
    // Duration 0.6s at the harness's 24 fps aligns to 17 spec frames (8n+1); the default continue
    // overlap (8) then resolves to a 9-frame window. StartSeconds 1.0 slices from frame 24.
    private const int SourcedClipFrames = 17;
    private const double SourcedClipDuration = 0.6;
    private const double SourcedStartSeconds = 1.0;

    private static readonly string[] SourcedClipFeatures =
        [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    private static JObject MakeSourcedClip(TestModelBundle models)
    {
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        clip["Duration"] = SourcedClipDuration;
        clip["SourceVideo"] = new JObject
        {
            ["Data"] = "data:video/mp4;base64," + Convert.ToBase64String([0x11, 0x22, 0x33]),
            ["FileName"] = "footage.mp4",
            ["StartSeconds"] = SourcedStartSeconds
        };
        return clip;
    }

    private static JObject MakeGeneratedClip(TestModelBundle models)
    {
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        clip["Duration"] = SourcedClipDuration;
        return clip;
    }

    private static (JObject Workflow, WorkflowGenerator Generator) GenerateSourcedFlow(
        TestModelBundle models, params JObject[] clips)
    {
        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, MakeRootConfig(512, 512, clips).ToString());
        return WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
    }

    private static SwarmFrameWindowNode AssertSourcedConformChain(WorkflowBridge bridge)
    {
        SwarmLoadVideoB64Node loadVideo = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Equal(loadVideo.Id, components.Video.Connection!.Node.Id);

        SwarmVideoResampleFPSNode resample = Assert.Single(
            bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.Equal(24.0, resample.FpsOut.LiteralAsDouble());
        Assert.Equal(components.Id, resample.FpsIn.Connection!.Node.Id);
        Assert.Equal(2, resample.FpsIn.Connection!.SlotIndex);
        Assert.Equal(components.Id, resample.ImagesInput.Connection!.Node.Id);
        Assert.Equal(0, resample.ImagesInput.Connection!.SlotIndex);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Equal((int)Math.Round(SourcedStartSeconds * 24), window.StartFrame.LiteralAsInt());
        Assert.Equal(SourcedClipFrames, window.FrameCount.LiteralAsInt());
        Assert.Equal(resample.Id, window.Images.Connection!.Node.Id);

        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            n => n.Image.Connection?.Node.Id == window.Id);
        Assert.Equal(512, scale.Width.LiteralAsInt());
        Assert.Equal(512, scale.Height.LiteralAsInt());

        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            n => ReachesUpstream(bridge, n, components.Id));
        Assert.Equal(SourcedStartSeconds, trim.StartIndex.LiteralAsDouble());
        Assert.Equal(SourcedClipFrames / 24.0, trim.Duration.LiteralAsDouble()!.Value, precision: 6);

        return window;
    }

    [Fact]
    public void Sourced_clip_replaces_generation_with_conform_chain_and_merges()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        (JObject workflow, WorkflowGenerator g) = GenerateSourcedFlow(
            models, MakeGeneratedClip(models), MakeSourcedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);

        // Per-clip refine: the sourced clip's single stage is a full passthrough (start step ==
        // steps, no denoise) fed by the conform chain, and its output joins the cross-clip merge.
        SwarmKSamplerNode sourcedSampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            sampler => ReachesUpstream(bridge, sampler, window.Id));
        Assert.Equal(10, sourcedSampler.StartAtStep.LiteralAsInt());
        Assert.Contains(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>(),
            batch => ReachesUpstream(bridge, batch, window.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_second_stage_refines_the_passthrough_footage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        ((JArray)sourced["Stages"]).Add(
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 12));
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        List<SwarmKSamplerNode> samplers = [.. SamplerNodesOrdered(bridge)
            .Where(s => ReachesUpstream(bridge, s, window.Id))];
        Assert.Equal(2, samplers.Count);
        Assert.Equal(10, samplers[0].StartAtStep.LiteralAsInt());
        Assert.Equal((int)Math.Floor(12 * 0.5), samplers[1].StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, samplers[0].Id),
            "Refine stage does not chain back to the passthrough stage's output.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_retake_masks_regenerate_a_window_of_the_footage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        sourced["Retake"] = new JObject
        {
            ["StartSeconds"] = 0.2,
            ["LengthSeconds"] = 0.2,
            ["Strength"] = 1.0
        };
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        Assert.True(
            ReachesUpstream(bridge, maskNode, window.Id),
            "Retake noise mask does not apply to the sourced footage's latent.");
        SwarmKSamplerNode sampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            s => ReachesUpstream(bridge, s, window.Id));
        Assert.Equal(0, sampler.StartAtStep.LiteralAsInt());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Continue_from_sourced_clip_freezes_its_tail_as_next_clip_context()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        sourced["BoundaryOut"] = "continue";
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, sourced, MakeGeneratedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        int windowFrames = Constants.ContinueOverlapDefaultFrames + 1;
        List<ImageFromBatchNode> tailSlices = [.. bridge.Graph.NodesOfType<ImageFromBatchNode>()
            .Where(n => n.BatchIndex.LiteralAsInt() == SourcedClipFrames - windowFrames
                && n.Length.LiteralAsInt() == windowFrames
                && ReachesUpstream(bridge, n, window.Id))];
        Assert.NotEmpty(tailSlices);

        Assert.Contains(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>(),
            n => n.Strength.LiteralAsDouble() == 1.0
                && tailSlices.Any(slice => ReachesUpstream(bridge, n, slice.Id)));

        SwarmRampMaskBatchNode ramp = Assert.Single(
            bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(windowFrames, ramp.Frames.LiteralAsInt());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Continue_into_sourced_clip_degrades_to_cut()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject generated = MakeGeneratedClip(models);
        generated["BoundaryOut"] = "continue";
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, generated, MakeSourcedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Fixed footage can't be conditioned on the previous clip's tail: the boundary degrades to
        // a plain cut concat, so no seam-collapse blend or ramp mask exists. (ImgToVideoInplace
        // nodes still appear from ordinary stage conditioning, so their absence is not the signal.)
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Contains(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>(),
            batch => ReachesUpstream(bridge, batch, window.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Single_sourced_clip_outputs_the_conformed_footage_with_its_audio()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        (JObject workflow, WorkflowGenerator g) = GenerateSourcedFlow(
            models, MakeSourcedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        Assert.Equal(SourcedClipFrames, g.CurrentMedia.Frames);
        INodeOutput currentOutput = bridge.ResolvePath((JArray)g.CurrentMedia.Path);
        Assert.True(
            ReachesUpstream(bridge, currentOutput.Node, window.Id),
            "Final media does not trace back to the sourced footage chain.");
        SwarmKSamplerNode sampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            s => ReachesUpstream(bridge, s, window.Id));
        Assert.Equal(10, sampler.StartAtStep.LiteralAsInt());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip0_in_text_to_video_flow_encodes_the_footage_not_an_empty_latent()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel, MakeRootConfig(512, 512, MakeSourcedClip(models)).ToString());
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildTextToVideoSteps(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The text-to-video root replacement must not hijack a sourced clip's first stage: that
        // path built an EmptyLTXVLatentVideo and orphaned the whole conform chain, so the clip
        // rendered noise instead of the uploaded footage.
        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        SwarmKSamplerNode sampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            s => ReachesUpstream(bridge, s, window.Id));
        Assert.Equal(10, sampler.StartAtStep.LiteralAsInt());

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "Saved video does not trace back to the sourced footage chain.");
        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Audio.Connection!.Node, trim.Id),
            "Saved audio does not trace back to the source video's trimmed track.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip0_in_text_to_video_flow_takes_over_the_root_save_and_drops_the_root_generation()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel, MakeRootConfig(512, 512, MakeSourcedClip(models)).ToString());
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeTextToVideoStepsWithPreCoreVideo(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The native t2v root chain (sampler 200 / separate 201 / decode 202) must not survive as a
        // dangling generation: its save is retargeted onto the sourced clip's output and the rest
        // pruned.
        Assert.False(workflow.ContainsKey("200"));
        Assert.False(workflow.ContainsKey("201"));
        Assert.False(workflow.ContainsKey("202"));
        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "Saved video does not trace back to the sourced footage chain.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_without_duration_generates_normally()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        sourced.Remove("Duration");
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Without a clip duration the used range is undefined; the source video is dropped at parse
        // time and the clip's stages run as usual.
        Assert.Empty(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.NotEmpty(SamplerNodesOrdered(bridge));
    }
}
