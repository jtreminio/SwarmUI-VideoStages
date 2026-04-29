using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Native_stage_prompting_uses_videoclip_prompt_sections()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 10));
        string prompt = "global-only words <videoclip[0]>clip-zero words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        List<string> conditioningTexts = workflow.Properties()
            .Select(property => property.Value)
            .OfType<JObject>()
            .Select(node => $"{node["inputs"]?["text"]}")
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "null")
            .ToList();

        Assert.NotEmpty(conditioningTexts);
        Assert.Contains(conditioningTexts, text => text.Contains("clip-zero words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("global-only words"));
    }

    [Fact]
    public void Native_stage_prompting_falls_back_to_global_prompt_without_matching_videoclip_section()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global-only words <videoclip[1]>other-clip words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        List<string> conditioningTexts = workflow.Properties()
            .Select(property => property.Value)
            .OfType<JObject>()
            .Select(node => $"{node["inputs"]?["text"]}")
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "null")
            .ToList();

        Assert.NotEmpty(conditioningTexts);
        Assert.Contains(conditioningTexts, text => text.Contains("global-only words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("other-clip words"));
    }

    [Fact]
    public void Native_ltx_stage_prompting_uses_videoclip_prompt_sections()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global-only words <videoclip[0]>clip-zero words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false));

        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        JArray positiveRef = WorkflowAssertions.RequireConnectionInput(conditioningNode.Node, "positive");
        WorkflowNode positiveEncoder = WorkflowAssertions.RequireNodeById(workflow, $"{positiveRef[0]}");
        Assert.Equal("clip-zero words", $"{positiveEncoder.Node["inputs"]?["prompt"]}");
    }

    [Fact]
    public void Videoclip_scoped_lora_applies_only_to_target_clip()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_VideoClipLora.safetensors", "UnitTest_VideoClipLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        string stagesJson = new JArray(
            MakeClip(width: 512, height: 512, MakeStage(models.VideoModel.Name, "Generated", steps: 10)),
            MakeClip(width: 512, height: 512, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();
        string prompt = "global prompt <videoclip[1]><lora:UnitTest_VideoClipLora:0.5>";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        Assert.True(input.TryGet(T2IParamTypes.Loras, out List<string> parsedLoras));
        Assert.Contains("UnitTest_VideoClipLora", parsedLoras);
        Assert.True(input.TryGet(T2IParamTypes.LoraSectionConfinement, out List<string> parsedConfinements));
        Assert.Contains($"{VideoStagesExtension.SectionIdForClip(1)}", parsedConfinements);
        WorkflowNode loraLoader = Assert.Single(WorkflowAssertions.NodesOfAnyType(workflow, "LoraLoader", "LoraLoaderModelOnly"));
        string loraId = loraLoader.Id;
        List<JArray> positiveEncoderClips = WorkflowUtils.NodesOfType(workflow, "CLIPTextEncode")
            .Where(node => $"{node.Node["inputs"]?["text"]}" == "global prompt")
            .Select(node => WorkflowAssertions.RequireConnectionInput(node.Node, "clip"))
            .ToList();

        Assert.Equal(2, positiveEncoderClips.Count);
        Assert.Contains(positiveEncoderClips, clip => !OutputTracesBackToSource(workflow, clip, new JArray(loraId, 1)));
        Assert.Contains(positiveEncoderClips, clip => OutputTracesBackToSource(workflow, clip, new JArray(loraId, 1)));
    }

    [Fact]
    public void Controlnet_lora_dropdown_uses_ltx_ic_model_only_loader()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_ControlNetLora.safetensors", "UnitTest_ControlNetLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject clip = MakeClip(
            width: 512,
            height: 512,
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode icLora = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXICLoRALoaderModelOnly"));
        Assert.Equal("UnitTest_ControlNetLora.safetensors", $"{icLora.Node["inputs"]?["lora_name"]}");
        Assert.Empty(WorkflowAssertions.NodesOfAnyType(workflow, "LoraLoader", "LoraLoaderModelOnly"));

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        List<WorkflowInputConnection> samplerModelConsumers = WorkflowUtils.FindInputConnections(workflow, new JArray(icLora.Id, 0))
            .Where(connection => connection.InputName == "model" && samplers.Any(sampler => sampler.Id == connection.NodeId))
            .ToList();
        Assert.NotEmpty(samplerModelConsumers);
    }
}
