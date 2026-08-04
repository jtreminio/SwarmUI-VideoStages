using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Section-prose extraction (comma bracket grammar + fallback chain) and prompt-section-scoped lora confinement
/// for the <c>&lt;videoclip&gt;</c> tag family, ported from main and adapted to the Data-param architecture.
/// </summary>
public partial class StageFlowTests
{
    [Fact]
    public void Videoclip_processed_cid_stage_section_extracts_only_for_matching_flat_stage()
    {
        int stage0Sid = VideoStagesExtension.SectionIdForStage(0);
        string prompt = $"global preamble <videoclip//cid={stage0Sid}>exclusive-stage-zero";

        Assert.Equal(
            "exclusive-stage-zero",
            VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 0, 0).Trim());
        Assert.Contains(
            "global preamble",
            VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 1, 1).Trim());
        Assert.DoesNotContain(
            "exclusive-stage-zero",
            VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 1, 1));
    }

    [Fact]
    public void Videoclip_raw_clip_stage_predicate_matches_comma_bracket_syntax()
    {
        string prompt = "global <videoclip[0,0]>tiered";
        Assert.Equal("tiered", VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 0, 0).Trim());
        Assert.DoesNotContain("tiered", VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 1, 1));
        Assert.True(VideoClipPromptText.HasAnySectionForClip(prompt, 0));
    }

    [Fact]
    public void Videoclip_tag_only_section_falls_back_to_video_section_before_global()
    {
        int videoclipCid = Constants.SectionID_VideoClip;
        int videoCid = T2IParamInput.SectionID_Video;
        string processedPrompt =
            $"Main prompt<video//cid={videoCid}>Video Prompt<videoclip//cid={videoclipCid}>";
        string originalPrompt =
            "Main prompt<video>Video Prompt<videoclip><lora:LTX-2/ltx-2.3-22b-distilled-lora-384-1.1>";

        Assert.Equal(
            "Video Prompt",
            PromptParser.ExtractPrompt(processedPrompt, originalPrompt, clipIndex: 0).Trim());
    }

    [Fact]
    public void Videoclip_scoped_lora_applies_only_to_target_clip()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = TestStubModel.Create(loraHandler, "UnitTest_VideoClipLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        string stagesJson = new JArray(
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10)),
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();
        string prompt = "global prompt <videoclip[1]><lora:UnitTest_VideoClipLora:0.5>";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.True(input.TryGet(T2IParamTypes.Loras, out List<string> parsedLoras));
        Assert.Contains("UnitTest_VideoClipLora", parsedLoras);
        Assert.True(input.TryGet(T2IParamTypes.LoraSectionConfinement, out List<string> parsedConfinements));
        Assert.Contains($"{VideoStagesExtension.SectionIdForClip(1)}", parsedConfinements);
        ComfyNode loraLoader = Assert.Single(LoraLoaderNodesOf(bridge));
        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.False(ReachesUpstream(bridge, samplers[0], loraLoader.Id));
        Assert.True(ReachesUpstream(bridge, samplers[1], loraLoader.Id));
    }

    [Fact]
    public void Videoclip_bracket_clip_stage_prompt_lora_is_promoted_for_flat_stage_section()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = TestStubModel.Create(loraHandler, "UnitTest_VideoClipStageLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        string stagesJson = JsonSingleClipStages(
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
    public void Videoclip_bracket_orphan_stage_lora_does_not_bubble_into_existing_sibling_stage()
    {
        using SwarmUiTestContext testContext = new();
        WorkflowGenerator.AddModelGenStep(g =>
        {
            (g.LoadingModel, g.LoadingClip) = g.LoadLorasForConfinement(T2IParamInput.SectionID_Video, g.LoadingModel, g.LoadingClip);
        }, -10);
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel stage0Lora = TestStubModel.Create(loraHandler, "UnitTest_Stage0Lora.safetensors");
        T2IModel stage1OrphanLora = TestStubModel.Create(loraHandler, "UnitTest_Stage1OrphanLora.safetensors");
        loraHandler.Models[stage0Lora.Name] = stage0Lora;
        loraHandler.Models[stage1OrphanLora.Name] = stage1OrphanLora;

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global"
            + " <videoclip[0,0]><lora:UnitTest_Stage0Lora:1>"
            + " <videoclip[0,1]><lora:UnitTest_Stage1OrphanLora:1>";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.True(input.TryGet(T2IParamTypes.LoraSectionConfinement, out List<string> parsedConfinements));
        Assert.True(input.TryGet(T2IParamTypes.Loras, out List<string> parsedLoras));
        int orphanIndex = parsedLoras.IndexOf("UnitTest_Stage1OrphanLora");
        Assert.True(orphanIndex >= 0, "Expected the orphan LoRA to be present in the parsed list.");
        Assert.NotEqual($"{Constants.SectionID_VideoClip}", parsedConfinements[orphanIndex]);

        List<string> loraLoaderNames = [.. bridge.Graph.NodesOfType<LoraLoaderNode>()
            .Select(n => n.LoraName.LiteralAsString())];
        Assert.Contains(stage0Lora.Name, loraLoaderNames);
        Assert.DoesNotContain(stage1OrphanLora.Name, loraLoaderNames);
    }
}
