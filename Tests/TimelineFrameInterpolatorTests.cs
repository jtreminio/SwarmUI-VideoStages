using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Ltx2;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

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

    [Fact]
    public void Rife_interpolates_the_final_trim_once_before_publication()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        Configure(input, "RIFE", 2);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                features: ["variation_seed", "frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode rife = Assert.Single(NodesOfClass(bridge, "RIFE VFI"));
        SwarmTrimFramesNode trim = Assert.IsType<SwarmTrimFramesNode>(
            rife.FindInput("frames").Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        SwarmSaveAnimationWSNode[] saves =
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().ToArray();
        Assert.Equal(2, saves.Length);
        SwarmSaveAnimationWSNode preInterpolationSave = Assert.Single(
            saves,
            save => ReferenceEquals(trim, save.Images.Connection?.Node));
        Assert.Equal(24.0, preInterpolationSave.Fps.LiteralAsDouble());
        SwarmSaveAnimationWSNode finalSave = Assert.Single(
            saves,
            save => ReferenceEquals(rife, save.Images.Connection?.Node));
        Assert.Equal(48.0, finalSave.Fps.LiteralAsDouble());
        Assert.Equal(17, generator.CurrentMedia.Frames);
        Assert.Equal(48, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Equal(rife.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Do_not_save_suppresses_both_outputs_without_suppressing_interpolation()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 4);
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.DoNotSave, true);
        Configure(input, "RIFE", 2);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                features: ["variation_seed", "frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode rife = Assert.Single(NodesOfClass(bridge, "RIFE VFI"));
        SwarmTrimFramesNode trim = Assert.IsType<SwarmTrimFramesNode>(
            rife.FindInput("frames").Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(17, generator.CurrentMedia.Frames);
        Assert.Equal(48, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(rife.Id, $"{generator.CurrentMedia.Path[0]}");
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Film_routes_the_final_video_and_uses_endpoint_frame_arithmetic()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        Configure(input, "FILM", 3);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                features: ["variation_seed", "frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode film = Assert.Single(NodesOfClass(bridge, "FILM VFI"));
        Assert.Equal(3, film.FindInput("multiplier").LiteralAsInt());
        Assert.Equal(37, generator.CurrentMedia.Frames);
        Assert.Equal(72, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(film.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.Empty(NodesOfClass(bridge, "RIFE VFI"));
        AssertNoDanglingNodeRefs(workflow);
    }

    [Fact]
    public void Gimm_routes_through_its_model_loader_and_uses_endpoint_frame_arithmetic()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        Configure(input, "GIMM-VFI", 3);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                features:
                [
                    "variation_seed",
                    "frameinterps",
                    "frameinterps_gimmvfi",
                ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode loader = Assert.Single(NodesOfClass(bridge, "DownloadAndLoadGIMMVFIModel"));
        ComfyNode gimm = Assert.Single(NodesOfClass(bridge, "GIMMVFI_interpolate"));
        Assert.Same(loader, gimm.FindInput("gimmvfi_model").Connection?.Node);
        Assert.Equal("VAEDecode", gimm.FindInput("images").Connection?.Node?.ClassTypeName);
        Assert.Equal(3, gimm.FindInput("interpolation_factor").LiteralAsInt());
        Assert.Equal(37, generator.CurrentMedia.Frames);
        Assert.Equal(72, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(gimm.Id, $"{generator.CurrentMedia.Path[0]}");
        AssertNoDanglingNodeRefs(workflow);
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
    [InlineData("RIFE", "frameinterps")]
    [InlineData("FILM", "frameinterps")]
    [InlineData("GIMM-VFI", "frameinterps_gimmvfi")]
    public void Missing_method_feature_warns_and_skips_interpolation(
        string method,
        string expectedFeature)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        Configure(input, method, 2);
        string[] installed = method == "GIMM-VFI"
            ? ["variation_seed", "frameinterps"]
            : ["variation_seed"];

        AssertInterpolationWarningAndSkip(input, installed, expectedFeature);
    }

    [Fact]
    public void Gimm_without_the_common_interpolation_feature_warns_and_skips()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        Configure(input, "GIMM-VFI", 2);

        AssertInterpolationWarningAndSkip(
            input,
            ["variation_seed", "frameinterps_gimmvfi"],
            "frameinterps");
    }

    [Fact]
    public void Multiplier_one_is_a_noop_without_a_method_or_features()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, 1);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                features: ["variation_seed"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(InterpolationNodes(bridge));
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
    }

    [Fact]
    public void One_frame_timeline_does_not_change_the_final_save_rate()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        input.Set(T2IParamTypes.VideoFrames, 1);
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        Configure(input, "RIFE", 2);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WanSteps(),
                features: ["variation_seed", "frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(InterpolationNodes(bridge));
        Assert.Equal(1, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        SwarmSaveAnimationWSNode save =
            Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(24.0, save.Fps.LiteralAsDouble());
    }

    [Fact]
    public void Multi_Wan_interpolation_warns_and_skips()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(stage), MakeClip(stage)).ToString());
        Configure(input, "RIFE", 2);

        AssertInterpolationWarningAndSkip(
            input,
            ["variation_seed", "frameinterps"],
            "2 clips joined by 1 boundary");
    }

    [Fact]
    public void Multi_ltx_interpolation_warns_and_skips()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(MakeClip(stage), MakeClip(stage)).ToString());
        Configure(input, "RIFE", 2);

        AssertInterpolationWarningAndSkip(
            input,
            ["variation_seed", "frameinterps", Ltx2HostIntegration.FeatureFlag],
            "2 clips joined by 1 boundary");
    }

    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan22")]
    public void Mixed_interpolation_warns_and_skips(string firstFamily)
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
        Configure(input, "RIFE", 2);

        AssertInterpolationWarningAndSkip(
            input,
            ["variation_seed", "frameinterps", Ltx2HostIntegration.FeatureFlag],
            "2 clips joined by 1 boundary");
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
        if (sourceFrames == 1)
        {
            Assert.Same(videoPath, generator.CurrentMedia.Path);
            using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
            Assert.Empty(NodesOfClass(bridge, "RIFE VFI"));
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

    private static void AssertInterpolationWarningAndSkip(
        T2IParamInput input,
        IEnumerable<string> features,
        string expected)
    {
        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()),
                features);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(InterpolationNodes(bridge));
        List<string> warnings = Assert.IsType<List<string>>(
            generator.UserInput.ExtraMeta["parser_warnings"]);
        Assert.Contains(warnings, warning =>
            warning.Contains(expected, StringComparison.Ordinal)
            && warning.Contains("will be skipped", StringComparison.Ordinal));
    }

    private static T2IParamInput WanInput(TestModelBundle models) =>
        BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> WanSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static void Configure(T2IParamInput input, string method, int multiplier)
    {
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, multiplier);
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMethod, method);
    }

    private static IEnumerable<ComfyNode> NodesOfClass(
        WorkflowBridge bridge,
        string classType) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName == classType);

    private static IEnumerable<ComfyNode> InterpolationNodes(WorkflowBridge bridge) =>
        bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName is
            "RIFE VFI" or "FILM VFI" or "GIMMVFI_interpolate");
}
