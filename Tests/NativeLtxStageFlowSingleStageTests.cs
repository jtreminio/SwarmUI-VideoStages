using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

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

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();
        string prompt = "global-only words <video>video-only words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));

        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        JArray positiveRef = WorkflowAssertions.RequireConnectionInput(conditioningNode.Node, "positive");
        WorkflowNode positiveEncoder = WorkflowAssertions.RequireNodeById(workflow, $"{positiveRef[0]}");
        Assert.Equal("video-only words", $"{positiveEncoder.Node["inputs"]?["prompt"]}");
    }

    [Fact]
    public void Native_ltx_generated_reference_reuses_current_video_chain_even_if_current_vae_was_reset()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithCurrentVaeMismatch(models.BaseModel, attachAudioToCurrentMedia: false));

        StageRefStore store = new(generator);
        Assert.NotNull(store.Generated);
        Assert.NotNull(store.Generated.Vae);
        Assert.Equal(T2IModelClassSorter.CompatLtxv2.ID, store.Generated.Vae.Compat?.ID);

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
    }

    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public void Native_ltx_stage_ignores_legacy_image_reference_when_no_clip_refs_are_defined(string imageReference)
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, imageReference, control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        StageRefStore store = new(generator);

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode preprocessNode = Assert.Single(preprocessNodes);
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            store.Generated);

        List<WorkflowNode> imgToVideoNodes = WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode imgToVideoNode = Assert.Single(imgToVideoNodes);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));

        List<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode sampler = Assert.Single(samplers);
        AssertSamplerConsumesImgToVideoOutput(workflow, imgToVideoNode, sampler);
        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        AssertSamplerUsesConditioningNode(sampler, conditioningNode);

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"));

        IReadOnlyList<WorkflowNode> saveNodes = WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS");
        WorkflowNode saveNode = Assert.Single(saveNodes);
        Assert.Equal("9", saveNode.Id);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "images"),
            new JArray("202", 0)));

        IReadOnlyList<WorkflowNode> separateNodes = WorkflowUtils.NodesOfType(workflow, "LTXVSeparateAVLatent");
        Assert.True(separateNodes.Count >= 2);

        WorkflowNode finalVideoDecode = WorkflowAssertions.RequireNodeById(workflow, "202");
        Assert.Equal("VAEDecodeTiled", $"{finalVideoDecode.Node["class_type"]}");
        AssertLtxFinalTiledDecodeUsesUpdatedDefaults(finalVideoDecode);
        WorkflowNode finalSeparate = RequireRetargetedSeparateNode(workflow, finalVideoDecode);

        WorkflowNode finalAudioDecode = WorkflowAssertions.RequireNodeById(workflow, "203");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(finalAudioDecode.Node, "samples"),
            new JArray(finalSeparate.Id, 1)));
        AssertNoDanglingTiledVaeDecodes(workflow);
        AssertWorkflowHasNoCycles(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
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

        WorkflowNode preprocessNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess").OrderBy(node => int.Parse(node.Id)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            new JArray("60", 0)));
        WorkflowNode imgToVideoNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace").OrderBy(node => int.Parse(node.Id)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));
        Assert.Equal(0.35, imgToVideoNode.Node["inputs"]?.Value<double>("strength"));
    }

    [Fact]
    public void Missing_base2edit_edit_stage_reference_throws_runtime_error()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "edit0", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorkflowTestHarness.GenerateWithSteps(input, BuildNativeSteps(attachAudioToCurrentMedia: false)));
        Assert.Contains("Base2Edit stage 0 does not exist", error.Message);
    }

    [Fact]
    public void Native_ltx_generated_reference_on_live_output_skips_redundant_guide_reinjection()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));

        IReadOnlyList<WorkflowNode> samplerNodes = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode samplerNode = Assert.Single(samplerNodes);
        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        AssertSamplerUsesConditioningNode(samplerNode, conditioningNode);

        IReadOnlyList<WorkflowNode> tiledDecodeNodes = WorkflowUtils.NodesOfType(workflow, "VAEDecodeTiled");
        WorkflowNode finalVideoDecode = Assert.Single(tiledDecodeNodes);
        Assert.Equal("202", finalVideoDecode.Id);
        AssertLtxFinalTiledDecodeUsesUpdatedDefaults(finalVideoDecode);
        RequireRetargetedSeparateNode(workflow, finalVideoDecode);

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

        string stagesJson = new JArray(
            MakeStage(
                models.VideoModel.Name,
                "Base",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
                steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        StageRefStore store = new(generator);

        WorkflowNode preprocessNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess").OrderBy(node => int.Parse(node.Id)));
        JArray preprocessImageInput = WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image");

        AssertGuideReferenceResolvesToPreprocessInput(workflow, preprocessImageInput, store.Generated);
    }

    [Fact]
    public void Native_ltx_stage_uses_core_default_strength_for_img_to_video_inplace()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        System.Reflection.FieldInfo strengthField = typeof(VideoStagesExtension).GetField(nameof(VideoStagesExtension.LTXVImgToVideoInplaceStrength));
        Assert.NotNull(strengthField);
        T2IRegisteredParam<double> strengthParam = Assert.IsType<T2IRegisteredParam<double>>(strengthField.GetValue(null));
        Assert.False(strengthParam.Type.VisibleNormally);
        Assert.True(strengthParam.Type.DoNotPreview);
        input.Set(strengthParam, 0.35);

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));

        WorkflowNode imgToVideoNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Equal(1.0, imgToVideoNode.Node["inputs"]?.Value<double>("strength"));
    }

    [Fact]
    public void Native_ltx_final_decode_uses_core_vae_tiling_overrides()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAETileSize, 960);
        input.Set(T2IParamTypes.VAETileOverlap, 96);
        input.Set(T2IParamTypes.VAETemporalTileSize, 512);
        input.Set(T2IParamTypes.VAETemporalTileOverlap, 12);

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));

        WorkflowNode finalVideoDecode = WorkflowAssertions.RequireNodeById(workflow, "202");
        AssertLtxFinalTiledDecodeUsesTiling(finalVideoDecode, 960, 96, 512, 12);
    }

    [Fact]
    public void Native_ltx_wrapper_chain_reuses_decode_audio_and_save_without_duplicate_trim()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeStepsWithTrimWrapper(attachAudioToCurrentMedia: false));
        StageRefStore store = new(generator);
        IReadOnlyList<WorkflowNode> saveNodes = WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS");
        WorkflowNode saveNode = Assert.Single(saveNodes);
        Assert.Equal("9", saveNode.Id);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "images"),
            new JArray("204", 0)));

        List<WorkflowNode> trimNodes = WorkflowUtils.NodesOfType(workflow, "SwarmTrimFrames")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Single(trimNodes);
        Assert.Equal("204", trimNodes[0].Id);

        List<WorkflowNode> imageFromBatchNodes = WorkflowUtils.NodesOfType(workflow, "ImageFromBatch")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Single(imageFromBatchNodes);
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(imageFromBatchNodes[0].Node, "image"),
            store.Generated);
        Assert.False(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imageFromBatchNodes[0].Node, "image"),
            new JArray("204", 0)));

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode preprocessNode = Assert.Single(preprocessNodes);
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            store.Generated);

        List<WorkflowNode> imgToVideoNodes = WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode imgToVideoNode = Assert.Single(imgToVideoNodes);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));

        List<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode sampler = Assert.Single(samplers);
        AssertSamplerConsumesImgToVideoOutput(workflow, imgToVideoNode, sampler);
        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        AssertSamplerUsesConditioningNode(sampler, conditioningNode);

        IReadOnlyList<WorkflowNode> separateNodes = WorkflowUtils.NodesOfType(workflow, "LTXVSeparateAVLatent");
        Assert.True(separateNodes.Count >= 2);
        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsReuseOriginalAudio(workflow, originalSeparate);

        WorkflowNode finalVideoDecode = WorkflowAssertions.RequireNodeById(workflow, "202");
        Assert.Equal("VAEDecodeTiled", $"{finalVideoDecode.Node["class_type"]}");
        AssertLtxFinalTiledDecodeUsesUpdatedDefaults(finalVideoDecode);
        WorkflowNode finalSeparate = RequireRetargetedSeparateNode(workflow, finalVideoDecode);

        WorkflowNode finalAudioDecode = WorkflowAssertions.RequireNodeById(workflow, "203");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(finalAudioDecode.Node, "samples"),
            new JArray(finalSeparate.Id, 1)));
        AssertNoDanglingTiledVaeDecodes(workflow);
        AssertWorkflowHasNoCycles(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("204", 0)));
    }
}
