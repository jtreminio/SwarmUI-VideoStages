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

        bool ok = new ControlNetAudioCapture(generator)
            .TryGetCapturedAudio(0, out WGNodeData audio);

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

        bool ok = new ControlNetAudioCapture(generator)
            .TryGetCapturedAudio(1, out WGNodeData audio);

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

        bool ok = new ControlNetAudioCapture(generator)
            .TryGetCapturedAudio(0, out WGNodeData audio);

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

        ControlNetAudioCapture applicator = new(generator);

        Assert.True(applicator.TryGetCapturedAudio(0, out WGNodeData a0));
        Assert.True(JToken.DeepEquals(a0.Path, new JArray("301", 1)));

        Assert.False(applicator.TryGetCapturedAudio(1, out WGNodeData _));

        Assert.True(applicator.TryGetCapturedAudio(2, out WGNodeData a2));
        Assert.True(JToken.DeepEquals(a2.Path, new JArray("701", 1)));
    }

    /// <summary>
    /// The video-upstream gate is what declines the audio, not the capture loop giving up on this
    /// ControlNet: the image and apply captures for the same index are still recorded. Asserting
    /// only the audio key's absence would pass under a pipeline that never ran at all.
    /// </summary>
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

        Assert.False(
            generator.NodeHelpers.ContainsKey("videostages.controlnet.audio.0"),
            "Expected no ControlNet audio capture when no GetVideoComponents node is upstream.");
        Assert.True(
            generator.NodeHelpers.ContainsKey("videostages.controlnet.apply.0"),
            "The capture loop never reached this ControlNet, so declining its audio proves nothing.");
        Assert.True(
            generator.NodeHelpers.ContainsKey("videostages.controlnet.fullimage.0"),
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
