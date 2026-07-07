using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Native_stage_prompting_uses_clip_prompt_from_json()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 10));
        clip["Prompt"] = "clip-zero words";
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: "global-only words");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> conditioningTexts = bridge.Graph.NodesOfType<CLIPTextEncodeNode>()
            .Select(n => n.Text.LiteralAsString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.NotEmpty(conditioningTexts);
        Assert.Contains(conditioningTexts, text => text.Contains("clip-zero words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("global-only words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("videostages") || text.Contains("\"stages\""));
    }

    [Fact]
    public void Native_stage_prompting_falls_back_to_global_prompt_without_clip_or_stage_prompt()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: "global-only words");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> conditioningTexts = bridge.Graph.NodesOfType<CLIPTextEncodeNode>()
            .Select(n => n.Text.LiteralAsString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.NotEmpty(conditioningTexts);
        Assert.Contains(conditioningTexts, text => text.Contains("global-only words"));
    }

    [Fact]
    public void Stage_prompt_overrides_clip_prompt_from_json()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["Prompt"] = "stage-zero words";
        JObject clip = MakeClip(stage);
        clip["Prompt"] = "clip-zero words";
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: "global-only words");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> conditioningTexts = bridge.Graph.NodesOfType<CLIPTextEncodeNode>()
            .Select(n => n.Text.LiteralAsString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.Contains(conditioningTexts, text => text.Contains("stage-zero words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("clip-zero words"));
    }

    [Fact]
    public void Native_ltx_stage_prompting_uses_clip_prompt_from_json()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["Prompt"] = "clip-zero words";
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: "global-only words");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WorkflowNode conditioningWorkflowNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        LTXVConditioningNode conditioningNode = RequireTypedNode<LTXVConditioningNode>(bridge, conditioningWorkflowNode.Id);
        SwarmClipTextEncodeAdvancedNode positiveEncoder = (SwarmClipTextEncodeAdvancedNode)conditioningNode.PositiveInput.Connection!.Node;
        Assert.Equal("clip-zero words", positiveEncoder.Prompt.LiteralAsString());
    }

    [Fact]
    public void Clip_scoped_lora_applies_only_to_target_clip()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_VideoClipLora.safetensors", "UnitTest_VideoClipLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject clipOne = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clipOne["Loras"] = new JArray(new JObject { ["Name"] = "UnitTest_VideoClipLora", ["Weight"] = 0.5 });
        string stagesJson = new JArray(
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10)),
            clipOne
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: "global prompt");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode loraLoader = Assert.Single(LoraLoaderNodesOf(bridge));
        List<ComfyNode> positiveEncoderClipStarts = bridge.Graph.NodesOfType<CLIPTextEncodeNode>()
            .Where(node => node.Text.LiteralAsString() == "global prompt")
            .Select(node => node.Clip.Connection!.Node)
            .ToList();

        Assert.Equal(2, positiveEncoderClipStarts.Count);
        Assert.Contains(positiveEncoderClipStarts, start => !ReachesUpstream(bridge, start, loraLoader.Id));
        Assert.Contains(positiveEncoderClipStarts, start => ReachesUpstream(bridge, start, loraLoader.Id));
    }

    [Fact]
    public void Stage_scoped_lora_from_json_loads_for_its_stage()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_VideoClipStageLora.safetensors", "UnitTest_VideoClipStageLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["Loras"] = new JArray(new JObject { ["Name"] = "UnitTest_VideoClipStageLora", ["Weight"] = 0.5 });
        string stagesJson = JsonSingleClipStages(stage);

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: "global prompt");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode loraLoader = Assert.Single(LoraLoaderNodesOf(bridge));
        Assert.Contains(
            bridge.Graph.NodesOfType<LoraLoaderNode>(),
            n => n.LoraName.LiteralAsString().Contains("UnitTest_VideoClipStageLora"));
    }

    [Fact]
    public void Controlnet_lora_dropdown_uses_ltx_ic_model_only_loader()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_ControlNetLora.safetensors", "UnitTest_ControlNetLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXICLoRALoaderModelOnlyNode icLora = Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal("UnitTest_ControlNetLora.safetensors", icLora.LoraName.LiteralAsString());
        Assert.Empty(LoraLoaderNodesOf(bridge));

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        IReadOnlyList<(ComfyNode Node, INodeInput Input)> modelConsumers = bridge.Graph.FindInputsConnectedTo(icLora.Model);
        Assert.Contains(
            modelConsumers,
            connection => connection.Input.Name == "model" && samplers.Any(sampler => sampler.Id == connection.Node.Id));
    }
}
