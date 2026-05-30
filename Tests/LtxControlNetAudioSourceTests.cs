using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class LtxControlNetAudioSourceTests
{
    private static WorkflowGenerator CreateGenerator(JObject workflow)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow,
        };
        generator.CurrentAudioVae = new WGNodeData(
            new JArray("900", 0),
            generator,
            WGNodeData.DT_AUDIOVAE,
            T2IModelClassSorter.CompatLtxv2);
        return generator;
    }

    private static void AddGetVideoComponentsStub(JObject workflow, string nodeId)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new GetVideoComponentsNode(), nodeId);
    }

    [Fact]
    public void TryGetCapturedControlNetAudio_returns_audio_when_captured()
    {
        JObject workflow = [];
        AddGetVideoComponentsStub(workflow, "301");
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.NodeHelpers["videostages.controlnet.audio.0"] =
            new JArray("301", 1).ToString(Formatting.None);

        bool ok = new ControlNetApplicator(generator)
            .TryGetCapturedControlNetAudio(Constants.ControlNetSourceOne, out WGNodeData audio);

        Assert.True(ok);
        Assert.NotNull(audio);
        Assert.Equal(WGNodeData.DT_AUDIO, audio.DataType);
        Assert.True(JToken.DeepEquals(audio.Path, new JArray("301", 1)));
    }

    [Fact]
    public void TryGetCapturedControlNetAudio_returns_false_when_no_capture_exists()
    {
        JObject workflow = [];
        WorkflowGenerator generator = CreateGenerator(workflow);

        bool ok = new ControlNetApplicator(generator)
            .TryGetCapturedControlNetAudio(Constants.ControlNetSourceTwo, out WGNodeData audio);

        Assert.False(ok);
        Assert.Null(audio);
    }

    [Fact]
    public void TryGetCapturedControlNetAudio_returns_false_when_referenced_node_was_pruned()
    {
        JObject workflow = [];
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.NodeHelpers["videostages.controlnet.audio.0"] =
            new JArray("301", 1).ToString(Formatting.None);

        bool ok = new ControlNetApplicator(generator)
            .TryGetCapturedControlNetAudio(Constants.ControlNetSourceOne, out WGNodeData audio);

        Assert.False(ok);
        Assert.Null(audio);
    }

    [Fact]
    public void TryGetCapturedControlNetAudio_resolves_per_index_for_each_controlnet_source()
    {
        JObject workflow = [];
        AddGetVideoComponentsStub(workflow, "301");
        AddGetVideoComponentsStub(workflow, "701");
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.NodeHelpers["videostages.controlnet.audio.0"] =
            new JArray("301", 1).ToString(Formatting.None);
        generator.NodeHelpers["videostages.controlnet.audio.2"] =
            new JArray("701", 1).ToString(Formatting.None);

        ControlNetApplicator applicator = new(generator);

        Assert.True(applicator.TryGetCapturedControlNetAudio(Constants.ControlNetSourceOne, out WGNodeData a0));
        Assert.True(JToken.DeepEquals(a0.Path, new JArray("301", 1)));

        Assert.False(applicator.TryGetCapturedControlNetAudio(Constants.ControlNetSourceTwo, out WGNodeData _));

        Assert.True(applicator.TryGetCapturedControlNetAudio(Constants.ControlNetSourceThree, out WGNodeData a2));
        Assert.True(JToken.DeepEquals(a2.Path, new JArray("701", 1)));
    }

    [Fact]
    public void TryGetCapturedControlNetAudio_returns_false_for_blank_unknown_or_malformed_source()
    {
        JObject workflow = [];
        AddGetVideoComponentsStub(workflow, "301");
        WorkflowGenerator generator = CreateGenerator(workflow);
        generator.NodeHelpers["videostages.controlnet.audio.0"] =
            new JArray("301", 1).ToString(Formatting.None);

        ControlNetApplicator applicator = new(generator);

        Assert.False(applicator.TryGetCapturedControlNetAudio("", out _));
        Assert.False(applicator.TryGetCapturedControlNetAudio("   ", out _));
        Assert.False(applicator.TryGetCapturedControlNetAudio(null, out _));
        Assert.False(applicator.TryGetCapturedControlNetAudio("ControlNet 4", out _));
        Assert.False(applicator.TryGetCapturedControlNetAudio("garbage", out _));
    }

    [Fact]
    public void Capture_records_audio_path_when_GetVideoComponents_is_upstream_of_controlnet()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(
            controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = models.VideoModel.ModelClass.CompatClass,
            },
        };

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["AudioSource"] = Constants.AudioSourceControlNet;
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject _, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoControlNet(controlNetModel));

        Assert.True(
            generator.NodeHelpers.TryGetValue("videostages.controlnet.audio.0", out string encoded),
            "Expected ControlNet 1 audio capture to be recorded.");
        JArray path = Assert.IsType<JArray>(JToken.Parse(encoded));
        Assert.Equal(2, path.Count);
        Assert.Equal("301", $"{path[0]}");
        Assert.Equal(1, (int)path[1]);
    }

    [Fact]
    public void Capture_records_audio_path_for_second_controlnet_index()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(
            controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = models.VideoModel.ModelClass.CompatClass,
            },
        };

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["AudioSource"] = Constants.AudioSourceControlNet;
        clip["ControlNetSource"] = Constants.ControlNetSourceTwo;
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[1].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[1].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[1], "UnitTestPreprocessor");

        (JObject _, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoControlNet(controlNetModel));

        Assert.True(
            generator.NodeHelpers.TryGetValue("videostages.controlnet.audio.1", out string encoded),
            "Expected ControlNet 2 audio capture to be recorded under index 1.");
        JArray path = Assert.IsType<JArray>(JToken.Parse(encoded));
        Assert.Equal("301", $"{path[0]}");
        Assert.Equal(1, (int)path[1]);

        Assert.False(
            generator.NodeHelpers.ContainsKey("videostages.controlnet.audio.0"),
            "ControlNet 1 audio should not be recorded when only ControlNet 2 is configured.");
    }

    [Fact]
    public void Capture_omits_audio_when_no_GetVideoComponents_upstream_of_controlnet()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(
            controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = models.VideoModel.ModelClass.CompatClass,
            },
        };

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);

        (JObject _, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNoGetVideoComponentsControlNetSteps(controlNetModel));

        Assert.False(
            generator.NodeHelpers.ContainsKey("videostages.controlnet.audio.0"),
            "Expected no ControlNet audio capture when no GetVideoComponents node is upstream.");
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithVideoControlNet(
        T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedVideoControlNetBranchWithGetVideoComponents(controlNetModel), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNoGetVideoComponentsControlNetSteps(
        T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedVideoControlNetBranchWithoutGetVideoComponents(controlNetModel), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedRefinerImageStep() =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            UnknownNode refinerImage = bridge.AddStub("UnitTest_RefinerImage", "200")
                .WithOutputs(WGNodeData.DT_IMAGE);
            g.CurrentMedia = refinerImage.GetOutput(0).ToWGMedia(g, WGNodeData.DT_IMAGE,
                width: 512, height: 512);
        }, 4.0);

    private static WorkflowGenerator.WorkflowGenStep SeedVideoControlNetBranchWithGetVideoComponents(
        T2IModel controlNetModel) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);

            SwarmLoadVideoB64Node videoLoad = new SwarmLoadVideoB64Node().With(VideoBase64: "unit-test-video");
            bridge.AddNode(videoLoad, "300");

            GetVideoComponentsNode videoComponents = new();
            videoComponents.Video.ConnectTo(videoLoad.VIDEO);
            bridge.AddNode(videoComponents, "301");

            ImageScaleNode scaled = new ImageScaleNode()
                .With(Width: 512, Height: 512, UpscaleMethod: "lanczos", Crop: "disabled");
            scaled.Image.ConnectTo(videoComponents.Images);
            bridge.AddNode(scaled, "302");

            UnknownNode preprocessor = bridge.AddStub("UnitTestPreprocessor", "303").WithOutputs(WGNodeData.DT_IMAGE);
            preprocessor.GetInput("image").ConnectToUntyped(scaled.IMAGE);

            ResizeImageMaskNodeNode resize = new ResizeImageMaskNodeNode
            {
                ExtraInputs = new JObject { ["resize_type.multiple"] = 8 },
            }.With(ResizeType: "scale to multiple", ScaleMethod: "lanczos");
            resize.Input.ConnectToUntyped(preprocessor.GetOutput(0));
            bridge.AddNode(resize, "304");

            ControlNetLoaderNode controlNetLoader = new ControlNetLoaderNode()
                .With(ControlNetName: controlNetModel.ToString(g.ModelFolderFormat));
            bridge.AddNode(controlNetLoader, "305");

            UnknownNode positive = bridge.AddStub("UnitTest_PositiveCond", "306").WithOutputs("CONDITIONING");
            UnknownNode negative = bridge.AddStub("UnitTest_NegativeCond", "307").WithOutputs("CONDITIONING");

            ControlNetApplyAdvancedNode controlApply = new ControlNetApplyAdvancedNode()
                .With(Strength: 0.8, StartPercent: 0.0, EndPercent: 1.0);
            controlApply.PositiveInput.ConnectToUntyped(positive.GetOutput(0));
            controlApply.NegativeInput.ConnectToUntyped(negative.GetOutput(0));
            controlApply.ControlNet.ConnectTo(controlNetLoader.CONTROLNET);
            controlApply.Image.ConnectToUntyped(resize.Resized);
            bridge.AddNode(controlApply, "308");

            g.FinalPrompt = new JArray("308", 0);
            g.FinalNegativePrompt = new JArray("308", 1);
        }, -6.1);

    private static WorkflowGenerator.WorkflowGenStep SeedVideoControlNetBranchWithoutGetVideoComponents(
        T2IModel controlNetModel) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);

            SwarmLoadVideoB64Node videoLoad = new SwarmLoadVideoB64Node().With(VideoBase64: "unit-test-video");
            bridge.AddNode(videoLoad, "300");

            // Untyped adapter stand-in for "video without GetVideoComponents wrapping" — keeps the
            // ControlNet image upstream traceable to a SwarmLoadVideoB64Node so the existing video
            // gate trips, while ensuring no GetVideoComponentsNode is on the upstream path.
            UnknownNode adapter = bridge.AddStub("UnitTest_VideoToImage", "301").WithOutputs(WGNodeData.DT_IMAGE);
            adapter.GetInput("video").ConnectToUntyped(videoLoad.VIDEO);

            ImageScaleNode scaled = new ImageScaleNode()
                .With(Width: 512, Height: 512, UpscaleMethod: "lanczos", Crop: "disabled");
            scaled.Image.ConnectToUntyped(adapter.GetOutput(0));
            bridge.AddNode(scaled, "302");

            UnknownNode preprocessor = bridge.AddStub("UnitTestPreprocessor", "303").WithOutputs(WGNodeData.DT_IMAGE);
            preprocessor.GetInput("image").ConnectToUntyped(scaled.IMAGE);

            ResizeImageMaskNodeNode resize = new ResizeImageMaskNodeNode
            {
                ExtraInputs = new JObject { ["resize_type.multiple"] = 8 },
            }.With(ResizeType: "scale to multiple", ScaleMethod: "lanczos");
            resize.Input.ConnectToUntyped(preprocessor.GetOutput(0));
            bridge.AddNode(resize, "304");

            ControlNetLoaderNode controlNetLoader = new ControlNetLoaderNode()
                .With(ControlNetName: controlNetModel.ToString(g.ModelFolderFormat));
            bridge.AddNode(controlNetLoader, "305");

            UnknownNode positive = bridge.AddStub("UnitTest_PositiveCond", "306").WithOutputs("CONDITIONING");
            UnknownNode negative = bridge.AddStub("UnitTest_NegativeCond", "307").WithOutputs("CONDITIONING");

            ControlNetApplyAdvancedNode controlApply = new ControlNetApplyAdvancedNode()
                .With(Strength: 0.8, StartPercent: 0.0, EndPercent: 1.0);
            controlApply.PositiveInput.ConnectToUntyped(positive.GetOutput(0));
            controlApply.NegativeInput.ConnectToUntyped(negative.GetOutput(0));
            controlApply.ControlNet.ConnectTo(controlNetLoader.CONTROLNET);
            controlApply.Image.ConnectToUntyped(resize.Resized);
            bridge.AddNode(controlApply, "308");

            g.FinalPrompt = new JArray("308", 0);
            g.FinalNegativePrompt = new JArray("308", 1);
        }, -6.1);

}
