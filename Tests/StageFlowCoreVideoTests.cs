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
    private static WorkflowGenerator.WorkflowGenStep SeedRootRawAudioAttachmentStep() =>
        new(g =>
        {
            string rawAudio = g.CreateNode("UnitTest_RawAudio", new JObject(), id: "50", idMandatory: false);
            g.CurrentMedia.AttachedAudio = new WGNodeData([rawAudio, 0], g, WGNodeData.DT_AUDIO, g.CurrentCompat());
        }, 10.8);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithRawAudio() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), SeedRootRawAudioAttachmentStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    [Fact]
    public void Enabled_video_stages_without_root_video_model_is_backend_noop()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated")
        ).ToString();

        T2IParamInput input = BuildInput(models.BaseModel, stagesJson, enableVideoStages: true);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNoopSteps());

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Empty(samplers);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS"));
        Assert.Equal(WGNodeData.DT_IMAGE, generator.CurrentMedia.DataType);
    }

    [Fact]
    public void Single_stage_on_native_video_workflow_adds_one_sampler_and_reuses_final_save()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Equal(2, samplers.Count);
        IReadOnlyList<WorkflowNode> saveNodes = WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS");
        WorkflowNode saveNode = Assert.Single(saveNodes);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "images"),
            generator.CurrentMedia.Path));
    }

    [Fact]
    public void Root_stage_resolution_inserts_center_crop_upscale_before_first_native_video_stage_batch_extract()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 768, height: 448, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode scaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray(scaleNode.Id, 0)));
        Assert.Equal(768, scaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(448, scaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("lanczos", $"{scaleNode.Node["inputs"]?["upscale_method"]}");
        Assert.Equal("center", $"{scaleNode.Node["inputs"]?["crop"]}");
    }

    [Fact]
    public void Root_stage_resolution_reuses_existing_upscale_before_first_native_video_stage_batch_extract()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeClip(
                width: 960,
                height: 544,
                MakeStage(models.VideoModel.Name, "Generated", upscale: 2.0, upscaleMethod: "pixel-bicubic", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode rootScaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node =>
                JToken.DeepEquals(
                    WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                    new JArray("12", 0)));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray(rootScaleNode.Id, 0)));
        WorkflowNode stageScaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node =>
                $"{node.Node["inputs"]?["upscale_method"]}" == "bicubic"
                && !JToken.DeepEquals(
                    WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                    new JArray("12", 0)));
        Assert.Equal(960, rootScaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(544, rootScaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("lanczos", $"{rootScaleNode.Node["inputs"]?["upscale_method"]}");
        Assert.Equal("center", $"{rootScaleNode.Node["inputs"]?["crop"]}");
        Assert.Equal("bicubic", $"{stageScaleNode.Node["inputs"]?["upscale_method"]}");
        Assert.Equal("disabled", $"{stageScaleNode.Node["inputs"]?["crop"]}");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(rootScaleNode.Id, 0)));
    }

    [Fact]
    public void Root_stage_resolution_updates_native_ltxv2_empty_latent_dimensions()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 768, height: 448, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode emptyLatentNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "EmptyLTXVLatentVideo"));
        Assert.Equal(768, emptyLatentNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(448, emptyLatentNode.Node["inputs"]?.Value<int>("height"));
    }

    [Fact]
    public void Root_stage_resolution_updates_native_ltxv2_audio_noise_mask_dimensions()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 384, height: 640, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.Width, 768);
        input.Set(T2IParamTypes.Height, 1280);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowStepsWithRawAudio());

        WorkflowNode setMaskNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "SetLatentNoiseMask"),
            node => WorkflowUtils.FindInputConnections(workflow, new JArray(node.Id, 0)).Any(connection =>
                connection.InputName == "audio_latent"
                && $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}" == "LTXVConcatAVLatent"));
        WorkflowNode concatNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVConcatAVLatent"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "audio_latent"),
                new JArray(setMaskNode.Id, 0)));
        WorkflowNode solidMaskNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(setMaskNode.Node, "mask")[0]}");

        Assert.Equal("SolidMask", $"{solidMaskNode.Node["class_type"]}");
        Assert.Equal(384, solidMaskNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(640, solidMaskNode.Node["inputs"]?.Value<int>("height"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(concatNode.Node, "audio_latent"),
            new JArray(setMaskNode.Id, 0)));
    }

    [Fact]
    public void Root_stage_resolution_still_applies_to_native_video_when_additional_stages_are_disabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        // Clip-shaped JSON still drives root resize even when the runner is disabled.
        string stagesJson = new JArray(
            MakeClip(width: 768, height: 448, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, enableVideoStages: false);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        WorkflowNode scaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray(scaleNode.Id, 0)));

        Assert.Single(samplers);
        Assert.Equal(768, scaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(448, scaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("lanczos", $"{scaleNode.Node["inputs"]?["upscale_method"]}");
        Assert.Equal("center", $"{scaleNode.Node["inputs"]?["crop"]}");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(scaleNode.Id, 0)));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
    }

    [Fact]
    public void Root_stage_resolution_updates_native_ltxv2_audio_noise_mask_dimensions_when_additional_stages_are_disabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 384, height: 640, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, enableVideoStages: false);
        input.Set(T2IParamTypes.Width, 768);
        input.Set(T2IParamTypes.Height, 1280);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowStepsWithRawAudio());

        WorkflowNode setMaskNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "SetLatentNoiseMask"),
            node => WorkflowUtils.FindInputConnections(workflow, new JArray(node.Id, 0)).Any(connection =>
                connection.InputName == "audio_latent"
                && $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}" == "LTXVConcatAVLatent"));
        WorkflowNode concatNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVConcatAVLatent"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "audio_latent"),
                new JArray(setMaskNode.Id, 0)));
        WorkflowNode solidMaskNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(setMaskNode.Node, "mask")[0]}");

        Assert.Equal("SolidMask", $"{solidMaskNode.Node["class_type"]}");
        Assert.Equal(384, solidMaskNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(640, solidMaskNode.Node["inputs"]?.Value<int>("height"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(concatNode.Node, "audio_latent"),
            new JArray(setMaskNode.Id, 0)));
    }

    [Fact]
    public void Root_stage_resolution_updates_native_wan22_latent_dimensions()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22VideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 832, height: 480, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.T5XXLModel, models.GemmaModel);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode rootScaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));
        WorkflowNode rootBatchNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray(rootScaleNode.Id, 0)));
        WorkflowNode wanLatentNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "Wan22ImageToVideoLatent"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "start_image"),
                new JArray(rootBatchNode.Id, 0)));
        Assert.Equal(832, wanLatentNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(480, wanLatentNode.Node["inputs"]?.Value<int>("height"));
    }

    [Fact]
    public void Root_guide_image_reference_edit_stage_retargets_root_scale_input_and_preserves_root_resolution()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 768, height: 448, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideImageReference, "edit0");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditImage(0));

        WorkflowNode scaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("60", 0)));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray(scaleNode.Id, 0)));
        Assert.Equal(768, scaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(448, scaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("lanczos", $"{scaleNode.Node["inputs"]?["upscale_method"]}");
        Assert.Equal("center", $"{scaleNode.Node["inputs"]?["crop"]}");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(scaleNode.Id, 0)));
    }

    [Fact]
    public void Root_guide_image_reference_default_keeps_current_root_source()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideImageReference, "Default");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditImage(0));

        Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));
        Assert.DoesNotContain(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("60", 0)));
    }

    [Fact]
    public void Root_guide_image_reference_base_still_applies_when_additional_stages_are_disabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        input.Set(VideoStagesExtension.RootGuideImageReference, "Base");
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        StageRefStore store = new(generator);
        WorkflowNode scaleNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ImageScale"));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray(scaleNode.Id, 0)));

        Assert.Single(samplers);
        Assert.NotNull(store.Base);
        Assert.False(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(scaleNode.Node, "image"),
            new JArray("12", 0)));
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(scaleNode.Node, "image"),
            store.Base);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(scaleNode.Id, 0)));
    }

    [Fact]
    public void Disabled_video_stages_ignores_configured_additional_stages()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, enableVideoStages: false);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Single(samplers);
        IReadOnlyList<WorkflowNode> saveNodes = WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS");
        WorkflowNode saveNode = Assert.Single(saveNodes);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "images"),
            generator.CurrentMedia.Path));
    }

    [Fact]
    public void Text_to_video_root_model_without_video_model_still_runs_configured_stage()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", steps: 10)
        ).ToString();

        T2IParamInput input = BuildTextToVideoInput(models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildTextToVideoSteps(attachAudioToCurrentMedia: true));

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Single(samplers);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);

        StageRefStore store = new(generator);
        Assert.NotNull(store.Generated);
    }

    [Fact]
    public void Two_stages_on_native_video_workflow_add_two_samplers_and_keep_single_final_save()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 14, cfgScale: 6.0, sampler: "dpmpp_2m", scheduler: "karras")
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Equal(3, samplers.Count);
        IReadOnlyList<WorkflowNode> saveNodes = WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS");
        WorkflowNode saveNode = Assert.Single(saveNodes);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "images"),
            generator.CurrentMedia.Path));
    }

    [Fact]
    public void Native_stage_prompting_uses_video_prompt_sections()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();
        string prompt = "global-only words <video>video-only words";

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson, prompt: prompt);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        List<string> conditioningTexts = workflow.Properties()
            .Select(property => property.Value)
            .OfType<JObject>()
            .Select(node => $"{node["inputs"]?["text"]}")
            .Where(text => !string.IsNullOrWhiteSpace(text) && text != "null")
            .ToList();

        Assert.NotEmpty(conditioningTexts);
        Assert.Contains(conditioningTexts, text => text.Contains("video-only words"));
        Assert.DoesNotContain(conditioningTexts, text => text.Contains("global-only words"));
    }
}
