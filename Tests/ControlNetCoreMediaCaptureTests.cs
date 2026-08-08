using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution.Graph;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ControlNetCoreMediaCaptureTests
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

    private static ControlNetCoreMediaCapture CaptureAudio(
        WorkflowGenerator generator,
        params (int Index, string NodeId)[] sources)
    {
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        T2IModelHandler handler = new() { ModelType = "ControlNet" };
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        foreach ((int index, string nodeId) in sources)
        {
            T2IModel model = TestStubModel.Create(
                handler,
                $"UnitTest_ControlNet_{index}.safetensors");
            generator.UserInput.Set(T2IParamTypes.Controlnets[index].Strength, 0.8);
            generator.UserInput.Set(T2IParamTypes.Controlnets[index].Model, model);
            GetVideoComponentsNode components = bridge.AddNode(
                new GetVideoComponentsNode(),
                nodeId);
            string loaderId = (int.Parse(nodeId) + 1).ToString();
            ControlNetLoaderNode loader = bridge.AddNode(
                new ControlNetLoaderNode().With(
                    ControlNetName: model.ToString(generator.ModelFolderFormat)),
                loaderId);
            ControlNetApplyAdvancedNode apply = new();
            apply.ControlNet.ConnectTo(loader.CONTROLNET);
            apply.Image.ConnectTo(components.Images);
            bridge.AddNode(apply, (int.Parse(nodeId) + 2).ToString());
        }
        ControlNetCoreMediaCapture capture = new(generator);
        capture.Capture();
        return capture;
    }

    [Fact]
    public void TryGetCapturedAudio_returns_audio_when_captured()
    {
        JObject workflow = [];
        WorkflowGenerator generator = CreateGenerator(workflow);
        ControlNetCoreMediaCapture capture = CaptureAudio(generator, (0, "301"));

        bool ok = capture.TryGetCapturedAudio(0, out WGNodeData audio);

        Assert.True(ok);
        Assert.NotNull(audio);
        Assert.Equal(WGNodeData.DT_AUDIO, audio.DataType);
        Assert.True(JToken.DeepEquals(audio.Path, new JArray("301", 1)));
    }

    [Fact]
    public void TryGetCapturedAudio_returns_false_when_no_capture_exists()
    {
        JObject workflow = [];
        WorkflowGenerator generator = CreateGenerator(workflow);

        bool ok = new ControlNetCoreMediaCapture(generator)
            .TryGetCapturedAudio(1, out WGNodeData audio);

        Assert.False(ok);
        Assert.Null(audio);
    }

    [Fact]
    public void TryGetCapturedAudio_returns_false_when_referenced_node_was_pruned()
    {
        JObject workflow = [];
        WorkflowGenerator generator = CreateGenerator(workflow);
        ControlNetCoreMediaCapture capture = CaptureAudio(generator, (0, "301"));
        workflow.Remove("301");

        bool ok = capture.TryGetCapturedAudio(0, out WGNodeData audio);

        Assert.False(ok);
        Assert.Null(audio);
    }

    [Fact]
    public void TryGetCapturedAudio_resolves_per_index_for_each_controlnet_source()
    {
        JObject workflow = [];
        WorkflowGenerator generator = CreateGenerator(workflow);
        ControlNetCoreMediaCapture capture = CaptureAudio(
            generator,
            (0, "301"),
            (2, "701"));

        Assert.True(capture.TryGetCapturedAudio(0, out WGNodeData a0));
        Assert.True(JToken.DeepEquals(a0.Path, new JArray("301", 1)));

        Assert.False(capture.TryGetCapturedAudio(1, out WGNodeData _));

        Assert.True(capture.TryGetCapturedAudio(2, out WGNodeData a2));
        Assert.True(JToken.DeepEquals(a2.Path, new JArray("701", 1)));
    }

    [Fact]
    public void Capture_omits_audio_when_no_GetVideoComponents_upstream_of_controlnet()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, TestStubModel.Folder(controlNetHandler), TestStubModel.File(controlNetHandler, "UnitTest_ControlNet.safetensors"), "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = models.VideoModel.ModelClass.CompatClass,
            },
        };

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);

        (JObject _, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNoGetVideoComponentsControlNetSteps(controlNetModel));

        ControlNetCoreMediaCapture capture = new(generator);
        Assert.False(
            capture.TryGetCapturedAudio(0, out WGNodeData _),
            "Expected no ControlNet audio capture when no GetVideoComponents node is upstream.");
        Assert.True(
            capture.TryGetCapturedControlImage(0, out WGNodeData _),
            "The capture loop never reached this ControlNet, so declining its audio proves nothing.");
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        Assert.True(
            capture.TryGetCapturedApplyImageInput(bridge, 0, out JArray _),
            "The capture loop never reached this ControlNet, so declining its audio proves nothing.");
    }

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

    private static WorkflowGenerator.WorkflowGenStep SeedVideoControlNetBranchWithoutGetVideoComponents(
        T2IModel controlNetModel) =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);

            SwarmLoadVideoB64Node videoLoad = new SwarmLoadVideoB64Node().With(VideoBase64: "unit-test-video");
            bridge.AddNode(videoLoad, "300");

            // The video gate passes, but no GetVideoComponents node exists to supply audio.
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


    [Fact]
    public void ControlNet_capture_skips_image_only_apply_and_recognizes_later_anima_video_apply()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        T2IModelHandler handler = new() { ModelType = "ControlNet" };
        T2IModel model = TestStubModel.Create(handler, "UnitTest_Anima_ControlNet.safetensors");
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, model);
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            ModelFolderFormat = "/",
            Workflow = new JObject(),
        };

        using WorkflowBridge bridge = BridgeSync.For(generator);
        ModelPatchLoaderNode loader = bridge.AddNode(
            new ModelPatchLoaderNode().With(Name: model.ToString(generator.ModelFolderFormat)),
            "1");
        UnknownNode image = bridge.AddStub("UnitTestImage", "2").WithOutputs(WGNodeData.DT_IMAGE);
        UnknownNode imageApply = bridge.AddStub("AnimaLLLiteApply", "3")
            .WithOutputs(WGNodeData.DT_MODEL);
        imageApply.GetInput("model_patch").ConnectToUntyped(loader.MODELPATCH);
        imageApply.GetInput("image").ConnectToUntyped(image.GetOutput(0));
        GetVideoComponentsNode video = bridge.AddNode(new GetVideoComponentsNode(), "4");
        UnknownNode videoApply = bridge.AddStub("AnimaLLLiteApply", "5")
            .WithOutputs(WGNodeData.DT_MODEL);
        videoApply.GetInput("model_patch").ConnectToUntyped(loader.MODELPATCH);
        videoApply.GetInput("image").ConnectToUntyped(video.Images);

        ControlNetCoreMediaCapture capture = new(generator);
        capture.Capture();

        Assert.True(capture.TryGetCapturedControlImage(0, out WGNodeData controlImage));
        Assert.Equal(WorkflowBridge.ToPath(video.Images), controlImage.Path);
        Assert.True(capture.TryGetCapturedApplyImageInput(bridge, 0, out JArray applyImage));
        Assert.Equal(WorkflowBridge.ToPath(video.Images), applyImage);
    }
}
