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

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithQwenControlNet(T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedCoreVideoQwenControlNetBranchStep(controlNetModel), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoQwenControlNetBranchStep(T2IModel controlNetModel) =>
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

            var modelPatchLoader = new ModelPatchLoaderNode()
                .With(Name: controlNetModel.ToString(g.ModelFolderFormat));
            bridge.AddNode(modelPatchLoader, "305");

            UnknownNode baseModelStub = bridge.AddStub("UnitTest_BaseModel", "306").WithOutputs(WGNodeData.DT_MODEL);
            UnknownNode baseVaeStub = bridge.AddStub("UnitTest_BaseVae", "307").WithOutputs(WGNodeData.DT_VAE);

            var qwenApply = new QwenImageDiffsynthControlnetNode().With(Strength: 0.8);
            qwenApply.Model.ConnectToUntyped(baseModelStub.GetOutput(0));
            qwenApply.ModelPatch.ConnectTo(modelPatchLoader.MODELPATCH);
            qwenApply.Vae.ConnectToUntyped(baseVaeStub.GetOutput(0));
            qwenApply.Image.ConnectToUntyped(resize.Resized);
            bridge.AddNode(qwenApply, "308");

            UnknownNode positive = bridge.AddStub("UnitTest_PositiveCond", "309").WithOutputs("CONDITIONING");
            UnknownNode negative = bridge.AddStub("UnitTest_NegativeCond", "310").WithOutputs("CONDITIONING");

            g.FinalPrompt = new JArray(positive.Id, 0);
            g.FinalNegativePrompt = new JArray(negative.Id, 0);
        }, -6.1);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithAliMamaControlNet(
        T2IModel controlNetModel,
        bool insertSetUnionPassThrough) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedCoreVideoAliMamaControlNetBranchStep(controlNetModel, insertSetUnionPassThrough), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoAliMamaControlNetBranchStep(
        T2IModel controlNetModel,
        bool insertSetUnionPassThrough) =>
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

            INodeOutput controlNetSource = controlNetLoader.CONTROLNET;
            if (insertSetUnionPassThrough)
            {
                var setUnion = new SetUnionControlNetTypeNode().With(Type: "auto");
                setUnion.ControlNet.ConnectTo(controlNetLoader.CONTROLNET);
                bridge.AddNode(setUnion, "311");
                controlNetSource = setUnion.CONTROLNET;
            }

            UnknownNode baseVaeStub = bridge.AddStub("UnitTest_BaseVae", "307").WithOutputs(WGNodeData.DT_VAE);
            UnknownNode positive = bridge.AddStub("UnitTest_PositiveCond", "309").WithOutputs("CONDITIONING");
            UnknownNode negative = bridge.AddStub("UnitTest_NegativeCond", "310").WithOutputs("CONDITIONING");
            UnknownNode maskStub = bridge.AddStub("UnitTest_Mask", "312").WithOutputs("MASK");

            var aliApply = new ControlNetInpaintingAliMamaApplyNode()
                .With(Strength: 0.8, StartPercent: 0.0, EndPercent: 1.0);
            aliApply.PositiveInput.ConnectToUntyped(positive.GetOutput(0));
            aliApply.NegativeInput.ConnectToUntyped(negative.GetOutput(0));
            aliApply.ControlNet.ConnectToUntyped(controlNetSource);
            aliApply.Vae.ConnectToUntyped(baseVaeStub.GetOutput(0));
            aliApply.Image.ConnectToUntyped(resize.Resized);
            aliApply.Mask.ConnectToUntyped(maskStub.GetOutput(0));
            bridge.AddNode(aliApply, "308");

            g.FinalPrompt = new JArray(aliApply.Id, 0);
            g.FinalNegativePrompt = new JArray(aliApply.Id, 1);
        }, -6.1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ltx_ic_lora_controlnet_source_captures_image_from_alimama_inpainting_apply(bool withSetUnionPassThrough)
    {
        using SwarmUiTestContext _ = new();
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

        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 8));
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithAliMamaControlNet(controlNetModel, withSetUnionPassThrough));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides = bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>();
        Assert.NotEmpty(guides);

        foreach (LTXAddVideoICLoRAGuideNode guide in guides)
        {
            Assert.NotNull(guide.Image.Connection);
            ComfyNode imageSource = guide.Image.Connection.Node;
            Assert.True(
                ReachesVideoSourceUpstream(imageSource),
                $"LTXAddVideoICLoRAGuideNode '{guide.Id}' image input does not trace back to a video source.");
        }
    }

    [Fact]
    public void Ltx_ic_lora_controlnet_source_captures_image_from_qwen_diffsynth_controlnet_apply()
    {
        using SwarmUiTestContext _ = new();
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

        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 8));
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithQwenControlNet(controlNetModel));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides = bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>();
        Assert.NotEmpty(guides);

        foreach (LTXAddVideoICLoRAGuideNode guide in guides)
        {
            Assert.NotNull(guide.Image.Connection);
            ComfyNode imageSource = guide.Image.Connection.Node;
            Assert.True(
                ReachesVideoSourceUpstream(imageSource),
                $"LTXAddVideoICLoRAGuideNode '{guide.Id}' image input does not trace back to a video source.");
        }
    }

    private static bool ReachesVideoSourceUpstream(ComfyNode start)
    {
        if (start is null)
        {
            return false;
        }
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(start);
        visited.Add(start.Id);
        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is SwarmLoadVideoB64Node or GetVideoComponentsNode)
            {
                return true;
            }
            foreach (INodeInput input in current.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream && visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }
        return false;
    }
}
