using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Wan;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// The Wan slice end to end: a real generated workflow, built through SwarmUI's own
/// image-to-video construction, with the host's core video pass handed off to the clip.
/// </summary>
[Collection("VideoStagesTests")]
public class WanRuntimeFlowTests
{
    private sealed class PreflightSnapshot
    {
        internal WorkflowGenerator Generator { get; private set; }
        internal JObject Workflow { get; private set; }
        internal WGNodeData Media { get; private set; }
        internal Dictionary<string, string> NodeHelpers { get; private set; }

        internal WorkflowGenerator.WorkflowGenStep Step() => new(g =>
        {
            Generator = g;
            Workflow = (JObject)g.Workflow.DeepClone();
            Media = g.CurrentMedia;
            NodeHelpers = new(g.NodeHelpers);
        }, Constants.WorkflowStepPriority.PreflightRequest - 0.5);

        internal void AssertUnchanged()
        {
            Assert.True(JToken.DeepEquals(Workflow, Generator.Workflow));
            Assert.Same(Media, Generator.CurrentMedia);
            Assert.Equal(NodeHelpers, Generator.NodeHelpers);
        }
    }

    [Fact]
    public void Wan_clip_generates_from_the_host_image_and_replaces_the_core_video()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        (JObject workflow, WorkflowGenerator generator) = GenerateWanClip(models, steps: 10);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The clip builds its own model and text encoders. The core video pass this clip replaces
        // left the host's loader cache pointing at nodes the root handoff pruned, so reusing that
        // cache would leave the sampler wired to nodes that are no longer in the graph.
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);

        // One Wan conditioning node, not two: the host's own core pass built one first and the
        // handoff pruned it, so only the clip's generation survives.
        ComfyNode imageToVideo = Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode sampler = Assert.Single(bridge.Graph.FindDownstream(imageToVideo.FindOutput(2)));
        Assert.Equal(10, sampler.FindInput("steps").LiteralAsInt());

        // The clip generates at the timeline resolution, from the still the host entered with
        // rather than the video the host made out of it.
        Assert.Single(
            NodesOfClass(bridge, "ImageScale"),
            node => node.FindInput("width").LiteralAsInt() == 512
                && node.FindInput("height").LiteralAsInt() == 512);

        // Wan owns no runtime key past its own host phases.
        Assert.DoesNotContain(
            generator.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
    }

    [Fact]
    public void Wan_clip_publishes_decoded_video_with_the_timeline_dimensions()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        (JObject workflow, WorkflowGenerator generator) = GenerateWanClip(models, steps: 6);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        // The host asked for 16 frames; Wan generates whole latent frames, so the graph makes 13
        // and the artifact reports what it makes rather than what was asked for.
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        // Timeline publication is architecture-neutral, so nothing downstream can branch on Wan.
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
    }

    [Fact]
    public void Hard_cut_clips_each_generate_from_the_host_image_and_save_the_intermediate()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(stage), MakeClip(stage)).ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);

        // Both clips enter from the same still with the same prompt and length, so they share one
        // conditioning node and differ only in the seed each samples with.
        ComfyNode imageToVideo = Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        long[] seeds = [.. bridge.Graph.FindDownstream(imageToVideo.FindOutput(2))
            .Select(node => node.FindInput("noise_seed").LiteralAsLong().Value)
            .Order()];
        Assert.Equal([1L + 42, 1L + 43], seeds);

        // Two clips means clip 0's video is not the timeline's own output, so the host's
        // intermediate-images setting has something to save alongside the merged timeline.
        ComfyNode merged = Assert.Single(NodesOfClass(bridge, BatchImagesNodeNode.ClassType));
        ComfyNode[] saves = [.. NodesOfClass(bridge, "SwarmSaveAnimationWS")];
        Assert.Equal(2, saves.Length);
        Assert.Single(saves, node => node.FindInput("images").Connection?.Node == merged);
    }

    [Fact]
    public void Ltx_then_Wan_hard_cut_keeps_both_family_graphs_and_publishes_one_neutral_join()
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        JObject document = MakeDocument(
            MakeClip(MakeStage(models.LtxVideoModel.Name, "Generated", steps: 7)),
            MakeClip(MakeStage(models.WanVideoModel.Name, "Generated", steps: 9)));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.LtxVideoModel,
            document.ToString());

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        Assert.NotEmpty(bridge.Graph.NodesOfType<LTXVConditioningNode>());
        Assert.Single(NodesOfClass(bridge, "WanImageToVideo"));
        BatchImagesNodeNode cut = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(cut.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(29, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);

        EmptyAudioNode wanSilence = Assert.Single(
            bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(13 / 24.0, wanSilence.Duration.LiteralAsDouble()!.Value, precision: 6);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        AudioConcatNode finalAudio = Assert.IsType<AudioConcatNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Same(wanSilence, finalAudio.Audio2.Connection!.Node);
        Assert.NotSame(wanSilence, finalAudio.Audio1.Connection!.Node);

        Assert.DoesNotContain(
            generator.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The host trims every image-to-video pass it builds, so a timeline that delegates to it once
    /// per clip has to drop those and keep only the trim over the finished timeline.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Global_frame_trim_is_applied_once_over_the_finished_timeline(int clipCount)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument([.. Enumerable.Range(0, clipCount).Select(_ => MakeClip(stage))])
                .ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);
        ComfyNode trim = Assert.Single(NodesOfClass(bridge, SwarmTrimFramesNode.ClassType));
        Assert.Equal(4, trim.FindInput("trim_start").LiteralAsInt());
        Assert.Equal(13 * clipCount - 4, generator.CurrentMedia.Frames);
    }

    /// <summary>
    /// These settings live in the host's video parameters rather than the authored clip document,
    /// so preflight is the only place they can be refused rather than ignored by this slice.
    /// </summary>
    [Theory]
    [InlineData("VideoSwapModel", "low-noise second pass")]
    [InlineData("VideoEndFrame", "first-to-last-frame generation")]
    [InlineData("Video2VideoCreativity", "partial-denoise generation")]
    [InlineData("FrameInterpolation", "frame interpolation")]
    public void Host_video_parameters_the_slice_cannot_honor_are_refused(
        string parameter,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        T2IParamInput input = WanInput(models, steps: 10);
        PreflightSnapshot snapshot = new();
        if (parameter == "VideoSwapModel")
        {
            input.Set(T2IParamTypes.VideoSwapModel, models.VideoModel);
        }
        else if (parameter == "VideoEndFrame")
        {
            input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));
        }
        else if (parameter == "Video2VideoCreativity")
        {
            input.Set(T2IParamTypes.Video2VideoCreativity, 0.5);
        }
        else
        {
            input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMethod, "RIFE");
            input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, 2);
        }

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));
        Assert.Contains(expectedReason, error.Message);
        Assert.DoesNotContain("request: VideoStages:", error.Message);
        snapshot.AssertUnchanged();
    }

    [Theory]
    [InlineData("missing-media", "marker is missing")]
    [InlineData("malformed-media", "marker is malformed")]
    [InlineData("removed-media-node", "was removed")]
    [InlineData("missing-snapshot", "snapshot is missing")]
    public void Corrupt_pre_core_handoff_fails_closed_and_cleans_every_Wan_key(
        string corruption,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator captured = null;
        WanRuntimeKeyScope keys = new();
        WorkflowGenerator.WorkflowGenStep remember =
            new(g => captured = g, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1);
        WorkflowGenerator.WorkflowGenStep corrupt = new(g =>
        {
            if (corruption == "missing-media")
            {
                g.NodeHelpers.Remove(keys.PreCoreMedia);
            }
            else if (corruption == "malformed-media")
            {
                g.NodeHelpers[keys.PreCoreMedia] = "not-a-marker";
            }
            else if (corruption == "missing-snapshot")
            {
                g.NodeHelpers.Remove(keys.PreCoreNodeIds);
            }
            else
            {
                string nodeId = g.NodeHelpers[keys.PreCoreMedia]
                    .Split(VideoGraphHelpers.MarkerSeparator)[0];
                using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
                bridge.RemoveNode(nodeId);
            }
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        remember,
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        corrupt,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains(expectedReason, error.Message);
        Assert.DoesNotContain(
            captured.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_refuses_an_unresolvable_root_without_leaving_partial_Wan_keys()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator captured = null;
        WorkflowGenerator.WorkflowGenStep breakRoot = new(g =>
        {
            captured = g;
            g.CurrentMedia = g.CurrentMedia.WithPath(new JArray("removed-root", 0));
        }, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.01);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([breakRoot, WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains("host root image is missing or no longer resolves", error.Message);
        Assert.DoesNotContain(
            captured.NodeHelpers.Keys,
            key => key.StartsWith("videostages.arch.wan22.", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_handoff_restores_an_exactly_absent_VAE()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        bool restoredNull = false;
        WorkflowGenerator.WorkflowGenStep clearRootVae = new(
            g => g.CurrentVae = null,
            Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1);
        WorkflowGenerator.WorkflowGenStep installCoreVae = new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            UnknownNode coreVae = bridge.AddStub("UnitTest_CoreVae", "wan-core-vae")
                .WithOutputs(WGNodeData.DT_VAE);
            g.CurrentVae = coreVae.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);
        WorkflowGenerator.WorkflowGenStep observe = new(
            g => restoredNull = g.CurrentVae is null,
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput + 0.01);

        WorkflowTestHarness.GenerateWithStepsAndState(
            WanInput(models, steps: 10),
            WorkflowTestHarness.Template_BaseOnlyImage()
                .Concat([
                    clearRootVae,
                    WorkflowTestHarness.CoreImageToVideoStep(),
                    installCoreVae,
                    observe,
                ])
                .Concat(WorkflowTestHarness.VideoStagesSteps()));

        Assert.True(restoredNull);
    }

    private static (JObject Workflow, WorkflowGenerator Generator) GenerateWanClip(
        TestModelBundle models,
        int steps) =>
        WorkflowTestHarness.GenerateWithStepsAndState(WanInput(models, steps), WanSteps());

    private static T2IParamInput WanInput(TestModelBundle models, int steps) =>
        BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated", steps: steps)));

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> WanSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<ComfyNode> NodesOfClass(WorkflowBridge bridge, string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);
}
