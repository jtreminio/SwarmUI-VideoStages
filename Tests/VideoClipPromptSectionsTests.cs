using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
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
    public void Videoclip_processed_cid_stage_section_extracts_only_for_matching_flat_stage()
    {
        int stage0Sid = VideoStagesExtension.SectionIdForStage(0);
        string prompt =
            $"global preamble <videoclip//cid={stage0Sid}>exclusive-stage-zero";
        Assert.Equal(
            "exclusive-stage-zero",
            PromptParser.ExtractPromptWithoutReferences(prompt, 0, 0, 0).Trim());
        Assert.Contains(
            "global preamble",
            PromptParser.ExtractPromptWithoutReferences(prompt, 0, 1, 1).Trim());
        Assert.DoesNotContain(
            "exclusive-stage-zero",
            PromptParser.ExtractPromptWithoutReferences(prompt, 0, 1, 1));
    }

    [Fact]
    public void Videoclip_raw_clip_stage_predicate_matches_bracket_syntax()
    {
        string prompt = @"global <videoclip[0,0]>tiered";
        Assert.Equal("tiered", PromptParser.ExtractPromptWithoutReferences(prompt, 0, 0, 0).Trim());
        Assert.DoesNotContain("tiered", PromptParser.ExtractPromptWithoutReferences(prompt, 0, 1, 1));
        Assert.True(PromptParser.HasAnyVideoClipSectionForClip(prompt, 0));
    }

    [Fact]
    public void Videoclip_prompt_section_stops_at_registered_custom_prompt_sections()
    {
        HashSet<string> customPartPrefixes = [.. PromptRegion.CustomPartPrefixes];
        List<string> partPrefixes = [.. PromptRegion.PartPrefixes];

        try
        {
            if (!PromptRegion.CustomPartPrefixes.Contains("unitcustom"))
            {
                PromptRegion.RegisterCustomPrefix("unitcustom");
            }

            string prompt = "global <videoclip[0]>clip-zero words <unitcustom>custom words";
            Assert.Equal("clip-zero words", PromptParser.ExtractPromptWithoutReferences(prompt, 0));
        }
        finally
        {
            PromptRegion.CustomPartPrefixes = customPartPrefixes;
            PromptRegion.PartPrefixes = partPrefixes;
        }
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
        List<JArray> positiveEncoderClips = WorkflowAssertions.NodesOfType(workflow, "CLIPTextEncode")
            .Where(node => $"{node.Node["inputs"]?["text"]}" == "global prompt")
            .Select(node => WorkflowAssertions.RequireConnectionInput(node.Node, "clip"))
            .ToList();

        Assert.Equal(2, positiveEncoderClips.Count);
        Assert.Contains(positiveEncoderClips, clip => !OutputTracesBackToSource(workflow, clip, new JArray(loraId, 1)));
        Assert.Contains(positiveEncoderClips, clip => OutputTracesBackToSource(workflow, clip, new JArray(loraId, 1)));
    }

    [Fact]
    public void Videoclip_bracket_clip_stage_prompt_lora_is_promoted_for_flat_stage_section()
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
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_VideoClipStageLora.safetensors", "UnitTest_VideoClipStageLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global prompt <videoclip[0,0]><lora:UnitTest_VideoClipStageLora:0.5>";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        Assert.True(input.TryGet(T2IParamTypes.Loras, out List<string> parsedLoras));
        Assert.Contains("UnitTest_VideoClipStageLora", parsedLoras);
        Assert.True(input.TryGet(T2IParamTypes.LoraSectionConfinement, out List<string> parsedConfinements));
        Assert.Contains($"{VideoStagesExtension.SectionIdForStage(0)}", parsedConfinements);
        Assert.NotEmpty(WorkflowAssertions.NodesOfAnyType(workflow, "LoraLoader", "LoraLoaderModelOnly"));
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

        WorkflowNode icLora = Assert.Single(WorkflowAssertions.NodesOfType(workflow, "LTXICLoRALoaderModelOnly"));
        Assert.Equal("UnitTest_ControlNetLora.safetensors", $"{icLora.Node["inputs"]?["lora_name"]}");
        Assert.Empty(WorkflowAssertions.NodesOfAnyType(workflow, "LoraLoader", "LoraLoaderModelOnly"));

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        List<WorkflowInputConnection> samplerModelConsumers = WorkflowAssertions.FindInputConnections(workflow, new JArray(icLora.Id, 0))
            .Where(connection => connection.InputName == "model" && samplers.Any(sampler => sampler.Id == connection.NodeId))
            .ToList();
        Assert.NotEmpty(samplerModelConsumers);
    }
}
