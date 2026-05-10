using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class LtxControlNetLoraDedupTests
{
    private static WorkflowGenerator.WorkflowGenStep SeedRefinerImageStep() =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            UnknownNode refinerImage = bridge.AddStub("UnitTest_RefinerImage", "12").WithOutputs(WGNodeData.DT_IMAGE);
            g.CurrentMedia = refinerImage.GetOutput(0).ToWGMedia(g, WGNodeData.DT_IMAGE,
                width: 512, height: 512);
        }, 5.0);

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoControlNetBranchStep(T2IModel controlNetModel) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);

            var videoLoad = new SwarmLoadVideoB64Node().With(VideoBase64: "unit-test-video");
            bridge.AddNode(videoLoad, "300");

            GetVideoComponentsNode videoComponents = new();
            videoComponents.Video.ConnectTo(videoLoad.VIDEO);
            bridge.AddNode(videoComponents, "301");

            var scaled = new ImageScaleNode()
                .With(Width: 512, Height: 512, UpscaleMethod: "lanczos", Crop: "disabled");
            scaled.Image.ConnectTo(videoComponents.Images);
            bridge.AddNode(scaled, "302");

            UnknownNode preprocessor = bridge.AddStub("UnitTestPreprocessor", "303").WithOutputs(WGNodeData.DT_IMAGE);
            preprocessor.GetInput("image").ConnectToUntyped(scaled.IMAGE);

            var resize = new ResizeImageMaskNodeNode
            {
                ExtraInputs = new JObject { ["resize_type.multiple"] = 8 }
            }.With(ResizeType: "scale to multiple", ScaleMethod: "lanczos");
            resize.Input.ConnectToUntyped(preprocessor.GetOutput(0));
            bridge.AddNode(resize, "304");

            var controlNetLoader = new ControlNetLoaderNode()
                .With(ControlNetName: controlNetModel.ToString(g.ModelFolderFormat));
            bridge.AddNode(controlNetLoader, "305");

            UnknownNode positive = bridge.AddStub("UnitTest_PositiveCond", "306").WithOutputs("CONDITIONING");
            UnknownNode negative = bridge.AddStub("UnitTest_NegativeCond", "307").WithOutputs("CONDITIONING");

            var controlApply = new ControlNetApplyAdvancedNode()
                .With(Strength: 0.8, StartPercent: 0.0, EndPercent: 1.0);
            controlApply.PositiveInput.ConnectToUntyped(positive.GetOutput(0));
            controlApply.NegativeInput.ConnectToUntyped(negative.GetOutput(0));
            controlApply.ControlNet.ConnectTo(controlNetLoader.CONTROLNET);
            controlApply.Image.ConnectToUntyped(resize.Resized);
            bridge.AddNode(controlApply, "308");

            g.FinalPrompt = new JArray("308", 0);
            g.FinalNegativePrompt = new JArray("308", 1);
        }, -6.1);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithVideoControlNet(T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedCoreVideoControlNetBranchStep(controlNetModel), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    [Fact]
    public void Ltx_controlnet_lora_loader_is_shared_across_stages_in_same_clip()
    {
        using SwarmUiTestContext _ = new();
        WorkflowTestHarness.VideoStagesSteps();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_ControlNetLora.safetensors", "UnitTest_ControlNetLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject stageA = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject stageB = MakeStage(models.VideoModel.Name, "PreviousStage", steps: 10);
        stageA["ControlNetStrength"] = 0.7;
        stageB["ControlNetStrength"] = 0.3;
        JObject clip = MakeClip(stageA, stageB);
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoControlNet(controlNetModel));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<LTXAddVideoICLoRAGuideNode> addGuideNodes = bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);

        LTXICLoRALoaderModelOnlyNode loraLoader = Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        foreach (SwarmKSamplerNode sampler in samplers)
        {
            Assert.True(
                ReachesUpstream(bridge, sampler, loraLoader.Id),
                $"Expected sampler {sampler.Id} model chain to route through LTXICLoRALoaderModelOnly node {loraLoader.Id}.");
        }
    }
}
