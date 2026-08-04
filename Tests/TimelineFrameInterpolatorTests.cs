using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// What the frame interpolator does that a real generated graph cannot show: refuse a request
/// before any phase mutates the workflow (asserted against a cloned pre-mutation snapshot), and
/// preserve the attached-audio object identity across an <c>Apply</c> driven straight off a
/// hand-built artifact. The graph-level contracts live in
/// <see cref="FrameInterpolationContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public sealed class TimelineFrameInterpolatorTests
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

    [Theory]
    [InlineData("missing-method", null, 2, "explicitly selected")]
    [InlineData("unknown-method", "Unknown-VFI", 2, "is not supported")]
    [InlineData("invalid-low", "RIFE", 0, "between 2 and 10")]
    [InlineData("invalid-high", "RIFE", 11, "between 2 and 10")]
    public void Invalid_active_configuration_is_refused_before_mutation(
        string _,
        string method,
        int multiplier,
        string expected)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, multiplier);
        if (method is not null)
        {
            input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMethod, method);
        }

        AssertPreflightFailure(input, ["variation_seed", "frameinterps"], expected);
    }

    [Theory]
    [InlineData(1, 1, 24)]
    [InlineData(3, 5, 48)]
    public void Applying_interpolation_preserves_audio_and_noops_a_single_frame(
        int sourceFrames,
        int expectedFrames,
        int expectedFps)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        Configure(input, "RIFE", 2);
        JObject workflow = [];
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = ["frameinterps"],
            Workflow = workflow,
        };
        WGNodeData video;
        WGNodeData audio;
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            UnknownNode videoNode = bridge.AddStub("UnitTest_FinalVideo", "video")
                .WithOutputs(WGNodeData.DT_VIDEO);
            UnknownNode audioNode = bridge.AddStub("UnitTest_FinalAudio", "audio")
                .WithOutputs(WGNodeData.DT_AUDIO);
            video = videoNode.GetOutput(0).ToWGNodeData(generator, WGNodeData.DT_VIDEO);
            audio = audioNode.GetOutput(0).ToWGNodeData(generator, WGNodeData.DT_AUDIO);
        }
        video.Width = 512;
        video.Height = 512;
        video.Frames = sourceFrames;
        video.FPS = 24;
        video.AttachedAudio = audio;
        JArray videoPath = video.Path;
        JArray audioPath = audio.Path;
        generator.CurrentMedia = video;

        new TimelineFrameInterpolator(generator).Apply();

        Assert.Same(audio, generator.CurrentMedia.AttachedAudio);
        Assert.Same(audioPath, generator.CurrentMedia.AttachedAudio.Path);
        Assert.Equal(expectedFrames, generator.CurrentMedia.Frames);
        Assert.Equal(expectedFps, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        using WorkflowBridge resultBridge = WorkflowBridge.Create(workflow);
        if (sourceFrames == 1)
        {
            Assert.Same(videoPath, generator.CurrentMedia.Path);
            Assert.Empty(NodesOfClass(resultBridge, "RIFE VFI"));
        }
        else
        {
            ComfyNode rife = Assert.Single(NodesOfClass(resultBridge, "RIFE VFI"));
            Assert.Equal(rife.Id, $"{generator.CurrentMedia.Path[0]}");
        }
    }

    private static void AssertPreflightFailure(
        T2IParamInput input,
        IEnumerable<string> features,
        string expected)
    {
        PreflightSnapshot snapshot = new();
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()),
                features));
        Assert.Contains(expected, error.Message);
        snapshot.AssertUnchanged();
    }

    private static T2IParamInput WanInput(TestModelBundle models) =>
        BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));

    private static void Configure(T2IParamInput input, string method, int multiplier)
    {
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, multiplier);
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMethod, method);
    }

    private static IEnumerable<ComfyNode> NodesOfClass(
        WorkflowBridge bridge,
        string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);
}
