using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Wan;
using VideoStages.Architectures.Wan.Planning;
using VideoStages.Generated;
using VideoStages.Planning;
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
        string loaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        string hostLoaderTuple = null;
        bool hostLoaderTupleWasInvalidated = false;
        WorkflowGenerator.WorkflowGenStep captureHostLoaderTuple = new(
            g => g.NodeHelpers.TryGetValue(loaderKey, out hostLoaderTuple),
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);
        WorkflowGenerator.WorkflowGenStep observeHandoffCleanup = new(
            g => hostLoaderTupleWasInvalidated = !g.NodeHelpers.ContainsKey(loaderKey),
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput + 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                WanInput(models, steps: 10),
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostLoaderTuple,
                        observeHandoffCleanup,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The host core pass populated its six-part loader tuple. The root handoff pruned those
        // nodes and invalidated the tuple before Wan asked the same host builder for its clip
        // graph, so no Wan-local delete is needed to avoid reusing dangling references.
        Assert.NotNull(hostLoaderTuple);
        Assert.Equal(6, hostLoaderTuple.Split(':').Length);
        Assert.True(hostLoaderTupleWasInvalidated);
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
    public void Two_Wan_stages_use_previous_decoded_video_and_exact_control_start_step()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel secondModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Second.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            "Generated",
            control: 1,
            steps: 10,
            cfgScale: 4);
        first.Remove("imageReference");
        JObject second = MakeStage(
            secondModel.Name,
            "PreviousStage",
            control: 0.35,
            steps: 12,
            cfgScale: 6.5,
            sampler: "dpmpp_2m",
            scheduler: "karras");
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second),
            prompt: "global <videoclip[0,0]>first-stage-prompt "
                + "<videoclip[0,1]>second-stage-prompt");
        VideoStagesSpec parsed = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(2, samplers.Length);
        ComfyNode firstSampler = Assert.Single(
            samplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == 43);
        ComfyNode secondSampler = Assert.Single(
            samplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == 44);
        AssertSamplerSettings(firstSampler, 10, 4, "euler", "normal");
        AssertSamplerSettings(secondSampler, 12, 6.5, "dpmpp_2m", "karras");
        Assert.Equal(0, firstSampler.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(12, 0.35),
            secondSampler.FindInput("start_at_step").LiteralAsInt());
        AssertSamplerModelSource(bridge, firstSampler, models.VideoModel.Name);
        AssertSamplerModelSource(bridge, secondSampler, secondModel.Name);

        VAEEncodeNode previousVideoEncode = Assert.IsType<VAEEncodeNode>(
            secondSampler.FindInput("latent_image").Connection?.Node);
        VAEDecodeNode previousStageDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => ReachesUpstream(bridge, previousVideoEncode, decode.Id)
                && ReachesUpstream(bridge, decode, firstSampler.Id));
        ComfyNode secondConditioning = secondSampler.FindInput("positive").Connection?.Node;
        Assert.Equal("WanImageToVideo", secondConditioning?.ClassTypeName);
        Assert.True(ReachesUpstream(
            bridge,
            secondConditioning.FindInput("start_image").Connection!.Node,
            previousStageDecode.Id));
        Assert.True(ReachesUpstream(bridge, previousStageDecode, firstSampler.Id));
        ComfyNode firstConditioning = firstSampler.FindInput("positive").Connection?.Node;
        Assert.Equal("WanImageToVideo", firstConditioning?.ClassTypeName);
        Assert.Contains(
            "first-stage-prompt",
            NodeText(firstConditioning.FindInput("positive").Connection?.Node));
        Assert.Contains(
            "second-stage-prompt",
            NodeText(secondConditioning.FindInput("positive").Connection?.Node));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Later_full_generation_uses_previous_first_frame_without_a_dead_source_encode()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 8),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 1,
                    steps: 10)));

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode first = SamplerBySeed(samplers, 43);
        ComfyNode second = SamplerBySeed(samplers, 44);
        Assert.Equal(0, second.FindInput("start_at_step").LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        ComfyNode secondConditioning = Assert.IsType<WanImageToVideoNode>(
            second.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge,
            secondConditioning.FindInput("start_image").Connection!.Node,
            first.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Json_partial_control_at_one_step_boundary_encodes_the_previous_decoded_video()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(models.VideoModel.Name, steps: 8);
        JObject second = MakeStage(models.VideoModel.Name, control: 0.87, steps: 8);
        first.Remove("imageReference");
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second));

        VideoStagesSpec parsed = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);
        Assert.Equal(1, WanStageSchedulePolicy.StartStep(
            parsed.Clips[0].Stages[1].Steps,
            parsed.Clips[0].Stages[1].Control));

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode firstSampler = SamplerBySeed(samplers, 43);
        ComfyNode secondSampler = SamplerBySeed(samplers, 44);
        Assert.Equal(1, secondSampler.FindInput("start_at_step").LiteralAsInt());
        VAEEncodeNode encodedPrevious = Assert.IsType<VAEEncodeNode>(
            secondSampler.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, encodedPrevious, firstSampler.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Three_Wan_stages_publish_intermediates_and_trim_only_the_final_output()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.25, steps: 12)));
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(3, samplers.Length);
        ComfyNode first = SamplerBySeed(samplers, 43);
        ComfyNode second = SamplerBySeed(samplers, 44);
        ComfyNode third = SamplerBySeed(samplers, 45);
        Assert.Equal(2, bridge.Graph.NodesOfType<VAEEncodeNode>().Count());
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(10, 0.5),
            second.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(
            WanStageSchedulePolicy.StartStep(12, 0.25),
            third.FindInput("start_at_step").LiteralAsInt());
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(
                second.FindInput("latent_image").Connection?.Node),
            first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(
                third.FindInput("latent_image").Connection?.Node),
            second.Id));

        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        Assert.Equal(3, saves.Length);
        Assert.Single(saves, save => ReferenceEquals(trim, save.Images.Connection?.Node));
        Assert.Equal(
            2,
            saves.Count(save => save.Images.Connection?.Node is VAEDecodeNode));
        Assert.All(saves, save => Assert.Equal(24.0, save.Fps.LiteralAsDouble()));
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(9, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Wan_swap_stage_chain_feeds_each_next_stage_from_the_prior_low_pass()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 11),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.8,
                    steps: 17)));
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(4, samplers.Length);
        ComfyNode firstHigh = Assert.Single(
            samplers,
            node => IsHighNoiseSampler(node)
                && node.FindInput("steps").LiteralAsInt() == 11);
        ComfyNode secondHigh = Assert.Single(
            samplers,
            node => IsHighNoiseSampler(node)
                && node.FindInput("steps").LiteralAsInt() == 17);
        ComfyNode firstLow = AssertLowNoiseForHigh(samplers, firstHigh);
        ComfyNode secondLow = AssertLowNoiseForHigh(samplers, secondHigh);
        int secondStart = WanStageSchedulePolicy.StartStep(17, 0.8);
        int secondHighEnd = WanStageSchedulePolicy.HostHighEndStep(17, 0.6);
        Assert.True(secondStart < secondHighEnd);
        Assert.Equal(secondStart, secondHigh.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(secondHighEnd, secondHigh.FindInput("end_at_step").LiteralAsInt());
        AssertSamplerSettings(firstLow, 14, 8.25, "dpmpp_2m", "karras");
        AssertSamplerSettings(secondLow, 14, 8.25, "dpmpp_2m", "karras");
        VAEEncodeNode secondInput = Assert.IsType<VAEEncodeNode>(
            secondHigh.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, secondInput, firstLow.Id));
        Assert.False(ReachesUpstream(bridge, secondInput, secondLow.Id));
        ComfyNode secondConditioning = secondHigh.FindInput("positive").Connection?.Node;
        Assert.Equal("WanImageToVideo", secondConditioning?.ClassTypeName);
        Assert.True(ReachesUpstream(
            bridge,
            secondConditioning.FindInput("start_image").Connection!.Node,
            firstLow.Id));
        INodeOutput finalOutput = bridge.ResolvePath(generator.CurrentMedia.Path);
        Assert.NotNull(finalOutput);
        Assert.True(ReachesUpstream(bridge, finalOutput.Node, secondLow.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Single_Wan_clip_uses_the_global_end_image_and_prunes_the_host_Flf_pass()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));
        string discardedHostFlfId = null;
        WorkflowGenerator.WorkflowGenStep captureHostFlf = new(g =>
        {
            discardedHostFlfId = Assert.Single(
                ((JObject)g.Workflow).Properties(),
                property => property.Value["class_type"]?.ToString()
                    == "WanFirstLastFrameToVideo").Name;
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureHostFlf,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(discardedHostFlfId);
        Assert.Null(workflow[discardedHostFlfId]);
        ComfyNode flf = Assert.Single(NodesOfClass(bridge, "WanFirstLastFrameToVideo"));
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));
        ComfyNode endScale = flf.FindInput("end_image").Connection?.Node;
        Assert.NotNull(endScale);
        Assert.Equal("ImageScale", endScale.ClassTypeName);
        Assert.Equal(512, endScale.FindInput("width").LiteralAsInt());
        Assert.Equal(512, endScale.FindInput("height").LiteralAsInt());
        Assert.Equal("lanczos", endScale.FindInput("upscale_method").LiteralAsString());
        LoadImageNode endLoad = Assert.IsType<LoadImageNode>(
            endScale.FindInput("image").Connection?.Node);
        Assert.Equal("${videoendframe}", endLoad.Image.LiteralAsString());

        ComfyNode startImage = flf.FindInput("start_image").Connection?.Node;
        Assert.NotNull(startImage);
        Assert.NotSame(endScale, startImage);
        Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => ReachesUpstream(bridge, startImage, decode.Id));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
    }

    [Fact]
    public void Wan_swap_composes_with_end_frame_and_global_low_noise_overrides()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = WanInput(models, steps: 11);
        input.Set(T2IParamTypes.VideoSwapModel, lowNoiseModel);
        input.Set(T2IParamTypes.VideoSwapPercent, 0.6);
        input.Set(T2IParamTypes.Steps, 14, T2IParamInput.SectionID_VideoSwap);
        input.Set(T2IParamTypes.CFGScale, 8.25, T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SamplerParam,
            "dpmpp_2m",
            T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SchedulerParam,
            "karras",
            T2IParamInput.SectionID_VideoSwap);
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x02], MediaType.ImagePng));
        string highLoaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        string lowLoaderKey = $"modelloader_{lowNoiseModel.Name}_image2video";
        string discardedHighTuple = null;
        string discardedLowTuple = null;
        bool discardedTuplesWereInvalidated = false;
        WorkflowGenerator.WorkflowGenStep captureDiscardedTuples = new(g =>
        {
            g.NodeHelpers.TryGetValue(highLoaderKey, out discardedHighTuple);
            g.NodeHelpers.TryGetValue(lowLoaderKey, out discardedLowTuple);
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);
        WorkflowGenerator.WorkflowGenStep observeHandoffCleanup = new(
            g => discardedTuplesWereInvalidated =
                !g.NodeHelpers.ContainsKey(highLoaderKey)
                && !g.NodeHelpers.ContainsKey(lowLoaderKey),
            Constants.WorkflowStepPriority.DropCoreImageToVideoOutput + 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([
                        WorkflowTestHarness.CoreImageToVideoStep(),
                        captureDiscardedTuples,
                        observeHandoffCleanup,
                    ])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.NotNull(discardedHighTuple);
        Assert.NotNull(discardedLowTuple);
        Assert.True(discardedTuplesWereInvalidated);
        string liveHighTuple = generator.NodeHelpers[highLoaderKey];
        string liveLowTuple = generator.NodeHelpers[lowLoaderKey];
        Assert.NotEqual(discardedHighTuple, liveHighTuple);
        Assert.NotEqual(discardedLowTuple, liveLowTuple);
        AssertLoaderTupleIsLive(workflow, liveHighTuple);
        AssertLoaderTupleIsLive(workflow, liveLowTuple);
        Assert.Equal(2, NodesOfClass(bridge, "WanFirstLastFrameToVideo").Count());
        Assert.Empty(NodesOfClass(bridge, "WanImageToVideo"));

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(2, samplers.Length);
        ComfyNode high = Assert.Single(samplers, IsHighNoiseSampler);
        ComfyNode low = AssertLowNoiseForHigh(samplers, high);

        Assert.Equal(11, high.FindInput("steps").LiteralAsInt());
        Assert.Equal(4.5, high.FindInput("cfg").LiteralAsDouble());
        Assert.Equal("euler", high.FindInput("sampler_name").LiteralAsString());
        Assert.Equal("normal", high.FindInput("scheduler").LiteralAsString());
        Assert.Equal(0, high.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(
            WanStageSchedulePolicy.HostHighEndStep(11, 0.6),
            high.FindInput("end_at_step").LiteralAsInt());
        Assert.Equal("enable", high.FindInput("return_with_leftover_noise").LiteralAsString());
        Assert.Equal("enable", high.FindInput("add_noise").LiteralAsString());
        Assert.Equal(43, high.FindInput("noise_seed").LiteralAsLong());

        Assert.Equal(14, low.FindInput("steps").LiteralAsInt());
        Assert.Equal(8.25, low.FindInput("cfg").LiteralAsDouble());
        Assert.Equal("dpmpp_2m", low.FindInput("sampler_name").LiteralAsString());
        Assert.Equal("karras", low.FindInput("scheduler").LiteralAsString());
        Assert.Equal((int)Math.Round(14 * (1 - 0.6)), low.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(10000, low.FindInput("end_at_step").LiteralAsInt());
        Assert.Equal("disable", low.FindInput("return_with_leftover_noise").LiteralAsString());
        Assert.Equal("disable", low.FindInput("add_noise").LiteralAsString());
        Assert.Equal(44, low.FindInput("noise_seed").LiteralAsLong());
        Assert.Same(high, low.FindInput("latent_image").Connection?.Node);

        AssertSamplerModelSource(bridge, high, models.VideoModel.Name);
        AssertSamplerModelSource(bridge, low, lowNoiseModel.Name);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        Assert.False(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                VideoStagesExtension.SectionIdForStage(0)));
        Assert.True(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                T2IParamInput.SectionID_VideoSwap));
    }

    [Fact]
    public void Two_Wan_swap_clips_keep_authored_high_settings_and_share_global_low_settings()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        JObject first = MakeStage(
            models.VideoModel.Name,
            steps: 11,
            cfgScale: 4,
            sampler: "euler",
            scheduler: "normal");
        JObject second = MakeStage(
            models.VideoModel.Name,
            steps: 17,
            cfgScale: 6.5,
            sampler: "dpmpp_2m",
            scheduler: "karras");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(first), MakeClip(second)).ToString());
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        Assert.Equal(4, samplers.Length);
        ComfyNode[] highs = [.. samplers.Where(IsHighNoiseSampler)];
        Assert.Equal(2, highs.Length);
        ComfyNode firstHigh = Assert.Single(
            highs,
            node => node.FindInput("steps").LiteralAsInt() == 11);
        ComfyNode secondHigh = Assert.Single(
            highs,
            node => node.FindInput("steps").LiteralAsInt() == 17);
        AssertSamplerSettings(firstHigh, 11, 4, "euler", "normal");
        AssertSamplerSettings(secondHigh, 17, 6.5, "dpmpp_2m", "karras");
        ComfyNode firstLow = AssertLowNoiseForHigh(samplers, firstHigh);
        ComfyNode secondLow = AssertLowNoiseForHigh(samplers, secondHigh);
        AssertSamplerSettings(firstLow, 14, 8.25, "dpmpp_2m", "karras");
        AssertSamplerSettings(secondLow, 14, 8.25, "dpmpp_2m", "karras");
        Assert.Equal(6, firstLow.FindInput("start_at_step").LiteralAsInt());
        Assert.Equal(6, secondLow.FindInput("start_at_step").LiteralAsInt());
        foreach (ComfyNode high in highs)
        {
            AssertSamplerModelSource(bridge, high, models.VideoModel.Name);
        }
        AssertSamplerModelSource(bridge, firstLow, lowNoiseModel.Name);
        AssertSamplerModelSource(bridge, secondLow, lowNoiseModel.Name);
        Assert.Single(
            NodesOfClass(bridge, "CheckpointLoaderSimple"),
            node => node.FindInput("ckpt_name").LiteralAsString() == models.VideoModel.Name);
        Assert.Single(
            NodesOfClass(bridge, "CheckpointLoaderSimple"),
            node => node.FindInput("ckpt_name").LiteralAsString() == lowNoiseModel.Name);

        string highLoaderKey = $"modelloader_{models.VideoModel.Name}_image2video";
        string lowLoaderKey = $"modelloader_{lowNoiseModel.Name}_image2video";
        AssertLoaderTupleIsLive(workflow, generator.NodeHelpers[highLoaderKey]);
        AssertLoaderTupleIsLive(workflow, generator.NodeHelpers[lowLoaderKey]);
        Assert.False(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                VideoStagesExtension.SectionIdForStage(0)));
        Assert.False(
            generator.UserInput.SectionParamOverrides.ContainsKey(
                VideoStagesExtension.SectionIdForStage(1)));
        Assert.Equal(26, generator.CurrentMedia.Frames);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Wan_swap_percent_endpoints_build_the_host_defined_split(double percent)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = WanInput(models, steps: 10);
        ConfigureSwap(input, lowNoiseModel, percent, lowSteps: 12);

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode high = Assert.Single(samplers, IsHighNoiseSampler);
        ComfyNode low = AssertLowNoiseForHigh(samplers, high);

        Assert.Equal(
            WanStageSchedulePolicy.HostHighEndStep(10, percent),
            high.FindInput("end_at_step").LiteralAsInt());
        Assert.Equal((int)Math.Round(12 * (1 - percent)), low.FindInput("start_at_step").LiteralAsInt());
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
        VideoModelProfileDescriptor profile =
            Assert.Single(WanArchitectureModule.Instance.Descriptor.Profiles);
        // The host asked for 16 frames; Wan generates whole latent frames, so the graph makes 13
        // according to its resolved profile metadata, and the artifact reports what it makes
        // rather than what was asked for.
        Assert.Equal(
            StaticGeneratedFrameGrid.SnapDown(16, profile.FrameGrid),
            generator.CurrentMedia.Frames);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        // Timeline publication is architecture-neutral, so nothing downstream can branch on Wan.
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
    }

    [Fact]
    public void Wan_clip_preserves_an_aligned_authored_generated_frame_count()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 6));
        clip["duration"] = 0.5;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(clip).ToString());

        (JObject _, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());

        VideoModelProfileDescriptor profile =
            Assert.Single(WanArchitectureModule.Instance.Descriptor.Profiles);
        Assert.True(StaticGeneratedFrameGrid.IsAligned(17, profile.FrameGrid));
        Assert.Equal(17, generator.CurrentMedia.Frames);
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

    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public void Mixed_hard_cut_pads_the_audio_disabled_Wan_clip_in_its_timeline_position(
        string firstFamily)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.WanVideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IModel first = firstFamily == "wan22"
            ? models.WanVideoModel
            : models.LtxVideoModel;
        T2IModel second = firstFamily == "wan22"
            ? models.LtxVideoModel
            : models.WanVideoModel;
        JObject document = MakeDocument(
            MakeClip(MakeStage(first.Name, "Generated", steps: 7)),
            MakeClip(MakeStage(second.Name, "Generated", steps: 9)));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            first,
            document.ToString());
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        EmptyAudioNode wanSilence = Assert.Single(
            bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(13 / 24.0, wanSilence.Duration.LiteralAsDouble()!.Value, precision: 6);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        AudioConcatNode finalAudio = Assert.IsType<AudioConcatNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        ComfyNode wanAudio = firstFamily == "wan22"
            ? finalAudio.Audio1.Connection!.Node
            : finalAudio.Audio2.Connection!.Node;
        ComfyNode otherAudio = firstFamily == "wan22"
            ? finalAudio.Audio2.Connection!.Node
            : finalAudio.Audio1.Connection!.Node;
        Assert.Same(wanSilence, wanAudio);
        Assert.NotSame(wanSilence, otherAudio);
        ComfyNode wanLow = Assert.Single(SamplerNodes(bridge), IsLowNoiseSampler);
        Assert.True(IsHighNoiseSampler(wanLow.FindInput("latent_image").Connection?.Node));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_Ltx_then_multi_stage_Wan_keeps_nested_and_timeline_lifecycles_separate(
        bool doNotSave)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.WanVideoModel, "UnitTest_Wan22_Low.safetensors");
        JObject document = MakeDocument(
            MakeClip(MakeStage(models.LtxVideoModel.Name, "Generated", steps: 7)),
            MakeClip(
                MakeStage(models.WanVideoModel.Name, "Generated", steps: 9),
                MakeStage(
                    models.WanVideoModel.Name,
                    "PreviousStage",
                    control: 0.8,
                    steps: 10)));
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.LtxVideoModel,
            document.ToString());
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);
        input.Set(T2IParamTypes.DoNotSave, doNotSave);
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode[] samplers = [.. SamplerNodes(bridge)];
        ComfyNode[] wanHighs =
        [
            .. samplers.Where(node =>
                IsHighNoiseSampler(node)
                && node.FindInput("positive").Connection?.Node?.ClassTypeName
                    == "WanImageToVideo"),
        ];
        Assert.Equal(2, wanHighs.Length);
        ComfyNode firstWanHigh = Assert.Single(
            wanHighs,
            node => node.FindInput("noise_seed").LiteralAsLong() == 44);
        ComfyNode secondWanHigh = Assert.Single(
            wanHighs,
            node => node.FindInput("noise_seed").LiteralAsLong() == 45);
        ComfyNode firstWanLow = AssertLowNoiseForHigh(samplers, firstWanHigh);
        ComfyNode secondWanLow = AssertLowNoiseForHigh(samplers, secondWanHigh);
        ComfyNode ltxSampler = Assert.Single(
            samplers,
            node => !ReachesUpstream(
                bridge,
                node.FindInput("positive").Connection?.Node,
                firstWanHigh.FindInput("positive").Connection?.Node?.Id)
                && node.FindInput("positive").Connection?.Node?.ClassTypeName
                    != "WanImageToVideo"
                && !IsLowNoiseSampler(node));

        ComfyNode firstWanConditioning =
            firstWanHigh.FindInput("positive").Connection?.Node;
        Assert.False(ReachesUpstream(
            bridge,
            firstWanConditioning.FindInput("start_image").Connection?.Node,
            ltxSampler.Id));
        VAEEncodeNode secondWanInput = Assert.IsType<VAEEncodeNode>(
            secondWanHigh.FindInput("latent_image").Connection?.Node);
        Assert.True(ReachesUpstream(bridge, secondWanInput, firstWanLow.Id));
        Assert.False(ReachesUpstream(bridge, secondWanInput, ltxSampler.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Same(merged, trim.Image.Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.True(ReachesUpstream(bridge, trim, secondWanLow.Id));

        EmptyAudioNode wanSilence = Assert.Single(
            bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(13 / 24.0, wanSilence.Duration.LiteralAsDouble()!.Value, precision: 6);
        TrimAudioDurationNode finalAudioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Equal(4 / 24.0, finalAudioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(25 / 24.0, finalAudioTrim.Duration.LiteralAsDouble()!.Value, precision: 6);
        AudioConcatNode finalAudio = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            concat => ReachesUpstream(bridge, finalAudioTrim, concat.Id));
        Assert.True(ReachesUpstream(
            bridge,
            finalAudio.Audio2.Connection?.Node,
            wanSilence.Id));
        Assert.False(ReachesUpstream(
            bridge,
            finalAudio.Audio1.Connection?.Node,
            wanSilence.Id));

        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        if (doNotSave)
        {
            Assert.Empty(saves);
        }
        else
        {
            Assert.Equal(3, saves.Length);
            Assert.Single(saves, save => ReferenceEquals(trim, save.Images.Connection?.Node));
            Assert.Contains(
                saves,
                save => ReachesUpstream(
                    bridge,
                    save.Images.Connection?.Node,
                    ltxSampler.Id));
            Assert.Contains(
                saves,
                save => ReachesUpstream(
                        bridge,
                        save.Images.Connection?.Node,
                        firstWanLow.Id)
                    && !ReachesUpstream(
                        bridge,
                        save.Images.Connection?.Node,
                        secondWanLow.Id));
        }
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
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
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument([.. Enumerable.Range(0, clipCount).Select(_ => MakeClip(stage))])
                .ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);
        ConfigureSwap(input, lowNoiseModel, percent: 0.6);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertNoDanglingNodeRefs(workflow);
        ComfyNode trim = Assert.Single(NodesOfClass(bridge, SwarmTrimFramesNode.ClassType));
        Assert.Equal(4, trim.FindInput("trim_start").LiteralAsInt());
        Assert.Equal(13 * clipCount - 4, generator.CurrentMedia.Frames);
        Assert.Equal(clipCount, SamplerNodes(bridge).Count(IsLowNoiseSampler));
    }

    /// <summary>
    /// These settings live in the host's video parameters rather than the authored clip document,
    /// so preflight is the only place they can be refused rather than ignored by this slice.
    /// </summary>
    [Fact]
    public void Wan_partial_denoise_is_refused_with_its_missing_source_provenance()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        T2IParamInput input = WanInput(models, steps: 10);
        PreflightSnapshot snapshot = new();
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.5);

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));
        Assert.Contains("stage 0 has no source-video donor", error.Message);
        Assert.DoesNotContain("request: VideoStages:", error.Message);
        snapshot.AssertUnchanged();
    }

    [Fact]
    public void Global_end_image_is_refused_before_mutation_for_two_Wan_clips()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(stage), MakeClip(stage)).ToString());
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "request-global and is ambiguous");
    }

    [Fact]
    public void Global_end_image_is_refused_before_mutation_for_multi_stage_Wan_clip()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 1, steps: 10),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: 0.5,
                    steps: 12)));
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "request-global and is ambiguous");
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public void Global_end_image_is_refused_before_mutation_for_mixed_timeline(
        string firstFamily)
    {
        using SwarmUiTestContext context = new();
        MixedVideoModelBundle models =
            TestModelFactory.CreateBaseLtxv2AndWan22ImageToVideoModels();
        T2IModel first = firstFamily == "wan22"
            ? models.WanVideoModel
            : models.LtxVideoModel;
        T2IModel second = firstFamily == "wan22"
            ? models.LtxVideoModel
            : models.WanVideoModel;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            first,
            MakeDocument(
                MakeClip(MakeStage(first.Name, "Generated", steps: 7)),
                MakeClip(MakeStage(second.Name, "Generated", steps: 9))).ToString());
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));

        AssertPreflightRefusalBeforeMutation(input, "request-global and is ambiguous");
    }

    [Fact]
    public void Incompatible_Wan_swap_model_is_refused_before_graph_mutation()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoSwapModel, models.BaseModel);

        AssertPreflightRefusalBeforeMutation(input, "not a supported Wan 2.2");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Invalid_Wan_swap_percent_is_refused_before_graph_mutation(double swapPercent)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel = AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = WanInput(models, steps: 10);
        input.Set(T2IParamTypes.VideoSwapModel, lowNoiseModel);
        input.Set(T2IParamTypes.VideoSwapPercent, swapPercent);

        AssertPreflightRefusalBeforeMutation(input, "must be finite and between 0 and 1");
    }

    [Theory]
    [InlineData(10, 0.5, 0.7, 1)]
    [InlineData(12, 0.5, 0.5, 0)]
    public void Wan_swap_refuses_an_empty_later_stage_high_noise_window_before_mutation(
        int steps,
        double control,
        double swapPercent,
        int expectedComparison)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IModel lowNoiseModel =
            AddDistinctWanModel(models.VideoModel, "UnitTest_Wan22_Low.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    control: control,
                    steps: steps)));
        ConfigureSwap(input, lowNoiseModel, swapPercent);

        int start = WanStageSchedulePolicy.StartStep(steps, control);
        int end = WanStageSchedulePolicy.HostHighEndStep(steps, swapPercent);
        Assert.Equal(expectedComparison, Math.Sign(start.CompareTo(end)));
        AssertPreflightRefusalBeforeMutation(input, "no high-noise sampling window");
    }

    [Theory]
    [InlineData(0, 8, "a stage that generates nothing")]
    [InlineData(0.9, 8, "quantizes to sampler start step 0")]
    public void Json_later_stage_control_is_refused_before_graph_mutation(
        double control,
        int steps,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject first = MakeStage(models.VideoModel.Name, steps: 8);
        JObject second = MakeStage(models.VideoModel.Name, control: control, steps: steps);
        first.Remove("imageReference");
        second.Remove("imageReference");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(first, second));

        VideoStagesSpec parsed = VideoStagesContext.GetVideoStagesSpecForPromptParse(input);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);
        AssertPreflightRefusalBeforeMutation(input, expectedReason);
    }

    [Theory]
    [InlineData(0.9, 8, false, 0.5, "quantizes to sampler start step 0")]
    [InlineData(0.5, 10, true, 0.7, "no high-noise sampling window")]
    public void Decoded_stage_adapter_rechecks_schedule_invariants_before_media_access(
        double control,
        int steps,
        bool withSwap,
        double swapPercent,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        WorkflowGenerator generator = new()
        {
            Workflow = new JObject(),
            UserInput = new(null),
        };
        WanStagePayload payload = new(
            models.VideoModel.Name,
            control,
            steps,
            4.5,
            "euler",
            "normal");
        StagePlan stage = new(
            StageId: 1,
            ClipStageIndex: 1,
            ClipStageRawIndex: 1,
            StageInputKind.PreviousStage,
            IsPassthrough: false,
            payload,
            new(
                IsTimelineTerminal: true,
                IntermediateOutputPolicy.NotEligible,
                PreserveConfiguredAudioTrackSave: false));
        ClipPlan clip = new(
            ClipId: 0,
            Frames: 13,
            ClipInputKind.RootMedia,
            IsSourced: false,
            SourceVideo: null,
            [stage],
            Audio: null)
        {
            Architecture = WanArchitectureModule.Instance.Descriptor,
            EntryMode = ArchitectureEntryMode.ImageToVideo,
            ArchitecturePayload = new WanClipPayload(0),
        };
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = generator,
            VideoSwapModel = withSwap ? models.VideoModel : null,
            VideoSwapPercent = swapPercent,
            Frames = 13,
            Steps = steps,
        };
        JObject before = (JObject)generator.Workflow.DeepClone();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new WanDecodedVideoStageInput(
                generator,
                (512, 512),
                24,
                new GlobalVideoFrameTrimmer(generator))
                .Configure(clip, stage, genInfo));

        Assert.Contains(expectedReason, error.Message);
        Assert.True(JToken.DeepEquals(before, generator.Workflow));
        Assert.Null(generator.CurrentMedia);
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

    private static IEnumerable<ComfyNode> SamplerNodes(WorkflowBridge bridge) =>
        NodesOfClass(bridge, "SwarmKSampler")
            .Concat(NodesOfClass(bridge, "KSamplerAdvanced"));

    private static ComfyNode SamplerBySeed(IEnumerable<ComfyNode> samplers, long seed) =>
        Assert.Single(
            samplers,
            node => node.FindInput("noise_seed").LiteralAsLong() == seed);

    private static string NodeText(ComfyNode node) =>
        node?.FindInput("text")?.LiteralAsString()
        ?? node?.FindInput("prompt")?.LiteralAsString();

    private static bool IsHighNoiseSampler(ComfyNode node) =>
        node is not null
        && node.FindInput("add_noise").LiteralAsString() == "enable"
        && node.FindInput("return_with_leftover_noise").LiteralAsString() == "enable";

    private static bool IsLowNoiseSampler(ComfyNode node) =>
        node is not null
        && node.FindInput("add_noise").LiteralAsString() == "disable"
        && node.FindInput("return_with_leftover_noise").LiteralAsString() == "disable";

    private static ComfyNode AssertLowNoiseForHigh(
        IEnumerable<ComfyNode> samplers,
        ComfyNode high) =>
        Assert.Single(
            samplers,
            node => IsLowNoiseSampler(node)
                && node.FindInput("latent_image").Connection?.Node == high);

    private static void AssertSamplerSettings(
        ComfyNode sampler,
        int steps,
        double cfg,
        string samplerName,
        string scheduler)
    {
        Assert.Equal(steps, sampler.FindInput("steps").LiteralAsInt());
        Assert.Equal(cfg, sampler.FindInput("cfg").LiteralAsDouble());
        Assert.Equal(samplerName, sampler.FindInput("sampler_name").LiteralAsString());
        Assert.Equal(scheduler, sampler.FindInput("scheduler").LiteralAsString());
    }

    private static void ConfigureSwap(
        T2IParamInput input,
        T2IModel lowNoiseModel,
        double percent,
        int lowSteps = 14)
    {
        input.Set(T2IParamTypes.VideoSwapModel, lowNoiseModel);
        input.Set(T2IParamTypes.VideoSwapPercent, percent);
        input.Set(T2IParamTypes.Steps, lowSteps, T2IParamInput.SectionID_VideoSwap);
        input.Set(T2IParamTypes.CFGScale, 8.25, T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SamplerParam,
            "dpmpp_2m",
            T2IParamInput.SectionID_VideoSwap);
        input.Set(
            ComfyUIBackendExtension.SchedulerParam,
            "karras",
            T2IParamInput.SectionID_VideoSwap);
    }

    private static T2IModel AddDistinctWanModel(T2IModel recognizedModel, string name)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, "/tmp", $"/tmp/{name}", name)
        {
            ModelClass = recognizedModel.ModelClass,
        };
        handler.Models[model.Name] = model;
        return model;
    }

    private static void AssertSamplerModelSource(
        WorkflowBridge bridge,
        ComfyNode sampler,
        string expectedModel)
    {
        ComfyNode loader = Assert.Single(
            bridge.Graph.FindUpstream(sampler),
            node => node.ClassTypeName == "CheckpointLoaderSimple");
        Assert.Equal(expectedModel, loader.FindInput("ckpt_name").LiteralAsString());
    }

    private static void AssertLoaderTupleIsLive(JObject workflow, string tuple)
    {
        string[] parts = tuple.Split(':');
        Assert.Equal(6, parts.Length);
        foreach (string nodeId in new[] { parts[0], parts[2], parts[4] }
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId)))
        {
            Assert.True(workflow[nodeId] is JObject);
        }
    }

    private static void AssertPreflightRefusalBeforeMutation(
        T2IParamInput input,
        string expectedReason)
    {
        PreflightSnapshot snapshot = new();
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));
        Assert.Contains(expectedReason, error.Message);
        snapshot.AssertUnchanged();
    }
}
