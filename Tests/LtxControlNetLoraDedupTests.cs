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

    private static (T2IModel ControlNetModel, T2IModel LoraModel) RegisterControlNetAndLora(T2IModel videoModel)
    {
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = videoModel.ModelClass.CompatClass
            }
        };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_ControlNetLora.safetensors", "UnitTest_ControlNetLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;
        return (controlNetModel, loraModel);
    }

    [Fact]
    public void Ltx_controlnet_lora_loader_is_created_per_stage_in_same_clip()
    {
        using SwarmUiTestContext testContext = new();
        WorkflowTestHarness.VideoStagesSteps();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        (T2IModel controlNetModel, _) = RegisterControlNetAndLora(models.VideoModel);

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

        List<LTXICLoRALoaderModelOnlyNode> loraLoaders = bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, loraLoaders.Count);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);

        Dictionary<string, LTXICLoRALoaderModelOnlyNode> samplerToIcLora = [];
        foreach (SwarmKSamplerNode sampler in samplers)
        {
            LTXICLoRALoaderModelOnlyNode owning = NearestModelInputIcLora(bridge, sampler);
            Assert.True(
                owning is not null,
                $"Expected sampler {sampler.Id} model chain to be fed by a LTXICLoRALoaderModelOnly.");
            samplerToIcLora[sampler.Id] = owning;
        }

        Assert.Equal(2, samplerToIcLora.Values.Select(node => node.Id).Distinct().Count());
    }

    private static LTXICLoRALoaderModelOnlyNode NearestModelInputIcLora(WorkflowBridge bridge, SwarmKSamplerNode sampler)
    {
        if (sampler.Model.Connection?.Node is not ComfyNode upstream)
        {
            return null;
        }
        return upstream is LTXICLoRALoaderModelOnlyNode direct
            ? direct
            : bridge.Graph.FindNearestUpstream<LTXICLoRALoaderModelOnlyNode>(upstream);
    }

    [Fact]
    public void Per_stage_scoped_lora_weights_thread_into_their_own_controlnet_lora_loader()
    {
        using SwarmUiTestContext testContext = new();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        (T2IModel controlNetModel, _) = RegisterControlNetAndLora(models.VideoModel);

        T2IModelHandler loraHandler = Program.T2IModelSets["LoRA"];
        T2IModel scopedLora = new(loraHandler, "/tmp", "/tmp/UnitTest_ScopedStageLora.safetensors", "UnitTest_ScopedStageLora.safetensors");
        loraHandler.Models[scopedLora.Name] = scopedLora;

        JObject stageA = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject stageB = MakeStage(models.VideoModel.Name, "PreviousStage", steps: 10);
        stageA["ControlNetStrength"] = 0.7;
        stageB["ControlNetStrength"] = 0.3;
        JObject clip = MakeClip(stageA, stageB);
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        string stagesJson = new JArray(clip).ToString();

        string prompt = "global prompt"
            + " <videoclip[0,0]><lora:UnitTest_ScopedStageLora:1>"
            + " <videoclip[0,1]><lora:UnitTest_ScopedStageLora:0.4>";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoControlNet(controlNetModel));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<LoraLoaderNode> loraLoaderNodes = [.. bridge.Graph.NodesOfType<LoraLoaderNode>()
            .Where(n => n.LoraName.LiteralAsString() == scopedLora.Name)
            .OrderBy(n => int.Parse(n.Id))];
        Assert.Equal(2, loraLoaderNodes.Count);

        foreach (LoraLoaderNode loraLoader in loraLoaderNodes)
        {
            IReadOnlyList<(ComfyNode Node, INodeInput Input)> modelConsumers =
                bridge.Graph.FindInputsConnectedTo(loraLoader.MODEL);
            Assert.True(
                modelConsumers.Count > 0,
                $"LoraLoader node {loraLoader.Id} (strength {loraLoader.StrengthModel.LiteralAsDouble()}) has a dangling MODEL output.");
        }

        List<LTXICLoRALoaderModelOnlyNode> icLoraLoaders = [.. bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>()
            .OrderBy(n => int.Parse(n.Id))];
        Assert.Equal(2, icLoraLoaders.Count);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);

        HashSet<string> seenLoraIds = [];
        List<double> reachedStrengths = [];
        foreach (SwarmKSamplerNode sampler in samplers)
        {
            LoraLoaderNode reachable = NearestModelInputLoraLoader(bridge, sampler, scopedLora.Name);
            Assert.True(
                reachable is not null,
                $"Expected sampler {sampler.Id} model chain to be fed by a scoped LoraLoader.");
            Assert.True(
                seenLoraIds.Add(reachable.Id),
                $"Sampler {sampler.Id} reuses LoraLoader {reachable.Id} that another sampler already claimed.");
            reachedStrengths.Add(reachable.StrengthModel.LiteralAsDouble() ?? double.NaN);
        }

        Assert.Contains(reachedStrengths, s => Math.Abs(s - 1.0) < 1e-4);
        Assert.Contains(reachedStrengths, s => Math.Abs(s - 0.4) < 1e-4);
    }

    private static LoraLoaderNode NearestModelInputLoraLoader(WorkflowBridge bridge, SwarmKSamplerNode sampler, string loraName)
    {
        ComfyNode current = sampler.Model.Connection?.Node;
        while (current is not null)
        {
            if (current is LoraLoaderNode loraLoader
                && loraLoader.LoraName.LiteralAsString() == loraName)
            {
                return loraLoader;
            }
            INodeInput modelInput = current.FindInput("model");
            current = modelInput?.Connection?.Node;
        }
        return null;
    }
}
