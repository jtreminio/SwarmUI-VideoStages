using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
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
    /// Neither parameter reaches clip compilation — both are read by the host's own image-to-video
    /// construction — so preflight is the only place they can be refused rather than ignored.
    /// </summary>
    [Theory]
    [InlineData("VideoSwapModel", "low-noise second pass")]
    [InlineData("VideoEndFrame", "first-to-last-frame generation")]
    public void Host_video_parameters_the_slice_cannot_honor_are_refused(
        string parameter,
        string expectedReason)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();

        T2IParamInput input = WanInput(models, steps: 10);
        if (parameter == "VideoSwapModel")
        {
            input.Set(T2IParamTypes.VideoSwapModel, models.VideoModel);
        }
        else
        {
            input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));
        }

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(input, WanSteps()));
        Assert.Contains(expectedReason, error.Message);
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
