using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Native_ltx_stage_prompting_uses_video_prompt_sections()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        string prompt = "global-only words <video>video-only words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WorkflowNode conditioningWorkflowNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        LTXVConditioningNode conditioningNode = RequireTypedNode<LTXVConditioningNode>(bridge, conditioningWorkflowNode.Id);
        SwarmClipTextEncodeAdvancedNode positiveEncoder = (SwarmClipTextEncodeAdvancedNode)conditioningNode.PositiveInput.Connection!.Node;
        Assert.Equal("video-only words", positiveEncoder.Prompt.LiteralAsString());
    }

    [Fact]
    public void Native_ltx_generated_reference_reuses_current_video_chain_even_if_current_vae_was_reset()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithCurrentVaeMismatch(models.BaseModel, attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        StageRefStore store = new(generator);
        Assert.NotNull(store.Generated);
        Assert.NotNull(store.Generated.Vae);
        Assert.Equal(T2IModelClassSorter.CompatLtxv2.ID, store.Generated.Vae.Compat?.ID);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
    }

    [Fact]
    public void Native_ltx_stage_can_use_base2edit_edit_stage_as_clip_ref_image()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10);
        stage["refStrengths"] = new JArray(0.35);
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 512, height: 512, refs: [MakeRef("edit0", frame: 1)], stage)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithPublishedBase2EditImage(0, attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVPreprocessNode preprocessNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().OrderBy(node => int.Parse(node.Id)));
        ImageScaleNode preprocessUpstream = (ImageScaleNode)preprocessNode.Image.Connection!.Node;
        Assert.Equal("60", preprocessUpstream.Image.Connection!.Node.Id);
        Assert.Equal(0, preprocessUpstream.Image.Connection.SlotIndex);
        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>().OrderBy(node => int.Parse(node.Id)));
        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);
        Assert.Equal(0.35, imgToVideoNode.Strength.LiteralAsDouble());
    }

    [Fact]
    public void Missing_base2edit_edit_stage_reference_skips_stage_without_throwing()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "edit0", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
    }

    [Fact]
    public void Native_ltx_generated_reference_on_live_output_skips_redundant_guide_reinjection()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        ComfyNode samplerNode = Assert.Single(SamplerNodesOrdered(bridge));
        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        AssertSamplerUsesConditioningNode(AsWorkflowNode(samplerNode, workflow), conditioningNode);

        VAEDecodeNode finalVideoDecode = RequireTypedNode<VAEDecodeNode>(bridge, "202");
        AssertLtxFinalDecodeUsesPlainVaeDecode(AsWorkflowNode(finalVideoDecode, workflow));
        RequireRetargetedSeparateNode(workflow, AsWorkflowNode(finalVideoDecode, workflow));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }

    [Fact]
    public void Native_ltx_zero_trim_parameters_do_not_insert_noop_trim_wrapper()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.TrimVideoStartFrames, 0);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 0);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }

    [Fact]
    public void Native_ltx_latent_model_upscale_keeps_core_default_guide_source_when_no_clip_refs_are_defined()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(
                models.VideoModel.Name,
                "Base",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
                steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        StageRefStore store = new(generator);

        LTXVPreprocessNode preprocessNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().OrderBy(node => int.Parse(node.Id)));

        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(preprocessNode.Image.Connection!),
            store.Generated);
    }

    [Fact]
    public void Native_ltx_stage_uses_core_default_strength_without_stage_ref_override()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Equal(1.0, imgToVideoNode.Strength.LiteralAsDouble());
    }

    [Fact]
    public void Native_ltx_final_decode_uses_core_vae_tiling_overrides()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAETileSize, 960);
        input.Set(T2IParamTypes.VAETileOverlap, 96);
        input.Set(T2IParamTypes.VAETemporalTileSize, 512);
        input.Set(T2IParamTypes.VAETemporalTileOverlap, 12);

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAEDecodeTiledNode finalVideoDecode = RequireTypedNode<VAEDecodeTiledNode>(bridge, "202");
        AssertLtxFinalTiledDecodeUsesTiling(AsWorkflowNode(finalVideoDecode, workflow), 960, 96, 512, 12);
    }

    [Fact]
    public void Native_ltx_wrapper_chain_reuses_decode_audio_and_save_without_duplicate_trim()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeStepsWithTrimWrapper(attachAudioToCurrentMedia: false));
        WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        StageRefStore store = new(generator);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal("9", saveNode.Id);
        Assert.Equal("204", saveNode.Images.Connection!.Node.Id);
        Assert.Equal(0, saveNode.Images.Connection.SlotIndex);

        SwarmTrimFramesNode trimNode = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>().OrderBy(node => int.Parse(node.Id)));
        Assert.Equal("204", trimNode.Id);

        ImageFromBatchNode imageFromBatchNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>().OrderBy(node => int.Parse(node.Id)));
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(imageFromBatchNode.Image.Connection!),
            store.Generated);
        Assert.NotEqual("204", imageFromBatchNode.Image.Connection!.Node.Id);

        LTXVPreprocessNode preprocessNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().OrderBy(node => int.Parse(node.Id)));
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(preprocessNode.Image.Connection!),
            store.Generated);

        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>().OrderBy(node => int.Parse(node.Id)));
        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);

        ComfyNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        AssertSamplerConsumesImgToVideoOutput(workflow, AsWorkflowNode(imgToVideoNode, workflow), AsWorkflowNode(sampler, workflow));
        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        AssertSamplerUsesConditioningNode(AsWorkflowNode(sampler, workflow), conditioningNode);

        IReadOnlyList<LTXVSeparateAVLatentNode> separateNodes = bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>();
        Assert.True(separateNodes.Count >= 2);
        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsReuseOriginalAudio(workflow, originalSeparate);

        VAEDecodeNode finalVideoDecode = RequireTypedNode<VAEDecodeNode>(bridge, "202");
        AssertLtxFinalDecodeUsesPlainVaeDecode(AsWorkflowNode(finalVideoDecode, workflow));
        WorkflowNode finalSeparate = RequireRetargetedSeparateNode(workflow, AsWorkflowNode(finalVideoDecode, workflow));

        LTXVAudioVAEDecodeNode finalAudioDecode = RequireTypedNode<LTXVAudioVAEDecodeNode>(bridge, "203");
        LTXVSeparateAVLatentNode finalSeparateTyped = RequireTypedNode<LTXVSeparateAVLatentNode>(bridge, finalSeparate.Id);
        Assert.Same(finalSeparateTyped.AudioLatent, finalAudioDecode.Samples.Connection);
        AssertNoDanglingTiledVaeDecodes(workflow);
        AssertWorkflowHasNoCycles(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("204", 0)));
    }
}
