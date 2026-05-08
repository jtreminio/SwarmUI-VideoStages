using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Native_stage_prompting_uses_videoclip_prompt_sections()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 10));
        string prompt = "global-only words <videoclip[0]>clip-zero words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> conditioningTexts = bridge.Graph.NodesOfType<CLIPTextEncodeNode>()
            .Select(n => n.Text.LiteralAsString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.NotEmpty(conditioningTexts);
        Assert.Contains(conditioningTexts, text => text.Contains("clip-zero words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("global-only words"));
    }

    [Fact]
    public void Native_stage_prompting_falls_back_to_global_prompt_without_matching_videoclip_section()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global-only words <videoclip[1]>other-clip words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<string> conditioningTexts = bridge.Graph.NodesOfType<CLIPTextEncodeNode>()
            .Select(n => n.Text.LiteralAsString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        Assert.NotEmpty(conditioningTexts);
        Assert.Contains(conditioningTexts, text => text.Contains("global-only words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("other-clip words"));
    }

    [Fact]
    public void Native_ltx_stage_prompting_uses_videoclip_prompt_sections()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global-only words <videoclip[0]>clip-zero words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
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
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10)),
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();
        string prompt = "global prompt <videoclip[1]><lora:UnitTest_VideoClipLora:0.5>";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.True(input.TryGet(T2IParamTypes.Loras, out List<string> parsedLoras));
        Assert.Contains("UnitTest_VideoClipLora", parsedLoras);
        Assert.True(input.TryGet(T2IParamTypes.LoraSectionConfinement, out List<string> parsedConfinements));
        Assert.Contains($"{VideoStagesExtension.SectionIdForClip(1)}", parsedConfinements);
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
    public void Videoclip_bracket_clip_stage_prompt_lora_is_promoted_for_flat_stage_section()
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

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global prompt <videoclip[0,0]><lora:UnitTest_VideoClipStageLora:0.5>";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.True(input.TryGet(T2IParamTypes.Loras, out List<string> parsedLoras));
        Assert.Contains("UnitTest_VideoClipStageLora", parsedLoras);
        Assert.True(input.TryGet(T2IParamTypes.LoraSectionConfinement, out List<string> parsedConfinements));
        Assert.Contains($"{VideoStagesExtension.SectionIdForStage(0)}", parsedConfinements);
        Assert.NotEmpty(LoraLoaderNodesOf(bridge));
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
