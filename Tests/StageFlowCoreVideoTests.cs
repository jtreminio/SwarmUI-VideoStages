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
    public void Configured_video_stages_without_root_video_model_is_backend_noop()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated")
        ).ToString();

        T2IParamInput input = BuildInput(models.BaseModel, stagesJson);
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
        Assert.Single(samplers);
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
        WorkflowNode imageFromBatch = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(scaleNode.Id, 0)));
        Assert.Equal(768, scaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(448, scaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("lanczos", $"{scaleNode.Node["inputs"]?["upscale_method"]}");
        Assert.Equal("center", $"{scaleNode.Node["inputs"]?["crop"]}");
    }

    [Fact]
    public void Root_stage_resolution_prefers_root_object_dimensions_over_legacy_clip_dimensions()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = MakeRootConfig(
            width: 1024,
            height: 576,
            MakeClip(width: 768, height: 448, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode scaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));

        Assert.Equal(1024, scaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(576, scaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("center", $"{scaleNode.Node["inputs"]?["crop"]}");
    }

    [Fact]
    public void Root_stage_resolution_prefers_registered_root_params_over_json_dimensions()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = MakeRootConfig(
            width: 1024,
            height: 576,
            MakeClip(width: 768, height: 448, MakeStage(models.VideoModel.Name, "Generated", steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootWidth, 1472);
        input.Set(VideoStagesExtension.RootHeight, 832);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode scaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));

        Assert.Equal(1472, scaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(832, scaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("center", $"{scaleNode.Node["inputs"]?["crop"]}");
    }

    [Fact]
    public void Root_stage_resolution_ignores_stage_upscale_json_before_first_native_video_stage_batch_extract()
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
        WorkflowNode imageFromBatch = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"));
        Assert.Equal(960, rootScaleNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(544, rootScaleNode.Node["inputs"]?.Value<int>("height"));
        Assert.Equal("lanczos", $"{rootScaleNode.Node["inputs"]?["upscale_method"]}");
        Assert.Equal("center", $"{rootScaleNode.Node["inputs"]?["crop"]}");
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(rootScaleNode.Id, 0)));
    }

    [Fact]
    public void Root_stage_takeover_does_not_leave_orphan_root_resolution_image_scale_after_pre_video_save()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
            1280,
            1024,
            MakeClipWithRefs(
                832,
                1216,
                [MakeRef("Base"), MakeRef("Base", frame: 2)],
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootWidth, 1280);
        input.Set(VideoStagesExtension.RootHeight, 1024);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPreVideoSave());

        Assert.Equal(1280, generator.CurrentMedia?.Width);
        Assert.Equal(1024, generator.CurrentMedia?.Height);

        foreach (WorkflowNode scale in WorkflowUtils.NodesOfType(workflow, "ImageScale"))
        {
            IReadOnlyList<WorkflowInputConnection> downstream = WorkflowUtils.FindInputConnections(
                workflow,
                new JArray(scale.Id, 0));
            Assert.NotEmpty(downstream);
        }
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
    public void Root_ltx_stage_uses_clip_duration_for_frame_count()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JObject
        {
            ["Width"] = 1280,
            ["Height"] = 1024,
            ["Clips"] = new JArray(
                new JObject
                {
                    ["Name"] = "Clip 0",
                    ["Duration"] = 30.0,
                    ["Stages"] = new JArray(
                        MakeStage(models.VideoModel.Name, "Generated", steps: 8))
                })
        }.ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFPS, 24);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode emptyLatentNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "EmptyLTXVLatentVideo"));
        Assert.Equal(720, emptyLatentNode.Node["inputs"]?.Value<int>("length"));
        Assert.All(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => Assert.Equal(720, node.Node["inputs"]?.Value<int>("length")));
    }

    [Fact]
    public void Core_video_workflow_uses_clip_zero_as_root_ltx_stage()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        List<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode sampler = Assert.Single(samplers);
        WorkflowNode preprocessNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess").OrderBy(node => int.Parse(node.Id)));
        WorkflowNode imgToVideoNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace").OrderBy(node => int.Parse(node.Id)));

        Assert.NotNull(WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));
        AssertSamplerConsumesImgToVideoOutput(workflow, imgToVideoNode, sampler);
    }

    [Fact]
    public void Root_ltx_stage_without_clip_refs_uses_core_default_img_to_video_strength()
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

        WorkflowNode rootScaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));
        WorkflowNode preprocessNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            new JArray(rootScaleNode.Id, 0)));

        WorkflowNode imgToVideoNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Equal(1.0, imgToVideoNode.Node["inputs"]?.Value<double>("strength"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));
    }

    [Fact]
    public void Root_ltx_stage_frame_one_clip_ref_uses_explicit_ref_source_when_it_differs_from_live_stage_input()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["refStrengths"] = new JArray(0.55);
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 768, height: 448, refs: [MakeRef("Base", frame: 1)], stage)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode rootScaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("12", 0)));
        WorkflowNode preprocessNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        StageRefStore store = new(generator);
        Assert.NotNull(store.Base);
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            store.Base);
        Assert.False(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            new JArray(rootScaleNode.Id, 0)));

        WorkflowNode imgToVideoNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Equal(0.55, imgToVideoNode.Node["inputs"]?.Value<double>("strength"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));
    }

    [Fact]
    public void Root_ltx_stage_frame_one_clip_ref_reuses_root_upscale_when_ref_matches_live_stage_input()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["refStrengths"] = new JArray(0.55);
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 768, height: 448, refs: [MakeRef("Base", frame: 1)], stage)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowStepsWithoutRefiner());

        WorkflowNode rootScaleNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ImageScale"));
        WorkflowNode preprocessNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            new JArray(rootScaleNode.Id, 0)));

        WorkflowNode imgToVideoNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Equal(0.55, imgToVideoNode.Node["inputs"]?.Value<double>("strength"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));
    }

    [Fact]
    public void Root_ltx_stage_frame_two_clip_ref_reuses_root_upscale_and_replaces_inplace_with_add_guide()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["refStrengths"] = new JArray(0.55);
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 768, height: 448, refs: [MakeRef("Base", frame: 2)], stage)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowStepsWithoutRefiner());

        WorkflowNode rootScaleNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ImageScale"));
        WorkflowNode preprocessNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            new JArray(rootScaleNode.Id, 0)));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));

        WorkflowNode addGuideNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));
        Assert.Equal(2, addGuideNode.Node["inputs"]?.Value<int>("frame_idx"));
        Assert.Equal(0.55, addGuideNode.Node["inputs"]?.Value<double>("strength"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(addGuideNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));

        WorkflowNode cropGuidesNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVCropGuides"));
        JArray cropLatentSource = WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "latent");
        Assert.True(workflow.TryGetValue($"{cropLatentSource[0]}", out JToken upstreamSampler));
        string upstreamSamplerType = $"{upstreamSampler["class_type"]}";
        Assert.True(
            upstreamSamplerType is "SwarmKSampler" or "KSamplerAdvanced",
            $"LTXVCropGuides.latent must follow the stage sampler (SwarmKSampler or KSamplerAdvanced); got {upstreamSamplerType}.");
        JArray cropPositiveIn = WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "positive");
        Assert.Equal(addGuideNode.Id, $"{cropPositiveIn[0]}");

        WorkflowNode separateAfterCrop = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVSeparateAVLatent"),
            node =>
            {
                JArray avIn = WorkflowAssertions.RequireConnectionInput(node.Node, "av_latent");
                return workflow.TryGetValue($"{avIn[0]}", out JToken upstream)
                    && $"{upstream["class_type"]}" == "LTXVCropGuides";
            });
        Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "VAEDecodeTiled"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "samples"),
                new JArray(separateAfterCrop.Id, 0)));
    }

    [Fact]
    public void Root_and_chained_ltx_stages_replace_inplace_with_add_guide_for_non_first_ref_frame()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stageA = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject stageB = MakeStage(models.VideoModel.Name, "PreviousStage", steps: 12);
        stageA["refStrengths"] = new JArray(0.55);
        stageB["refStrengths"] = new JArray(0.65);
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 768, height: 448, refs: [MakeRef("Base", frame: 2)], stageA, stageB)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowStepsWithoutRefiner());

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));

        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);
        Assert.Equal(2, addGuideNodes[0].Node["inputs"]?.Value<int>("frame_idx"));
        Assert.Equal(2, addGuideNodes[1].Node["inputs"]?.Value<int>("frame_idx"));
        Assert.Equal(0.55, addGuideNodes[0].Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(0.65, addGuideNodes[1].Node["inputs"]?.Value<double>("strength"));

        Assert.Equal(2, WorkflowUtils.NodesOfType(workflow, "LTXVCropGuides").Count);
    }

    [Fact]
    public void Chained_ltx_latent_model_upscale_uses_separated_video_latent_directly()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stageA = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject stageB = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            upscale: 1.5,
            upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
            steps: 12);
        stageA["refStrengths"] = new JArray(0.55);
        stageB["refStrengths"] = new JArray(0.65);
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 768, height: 448, refs: [MakeRef("Base", frame: 2)], stageA, stageB)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        WorkflowNode upsamplerNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVLatentUpsampler"));
        JArray upsamplerSamples = WorkflowAssertions.RequireConnectionInput(upsamplerNode.Node, "samples");
        WorkflowNode upsamplerSource = WorkflowAssertions.RequireNodeById(workflow, $"{upsamplerSamples[0]}");
        Assert.Equal("LTXVSeparateAVLatent", $"{upsamplerSource.Node["class_type"]}");
        Assert.Equal("0", $"{upsamplerSamples[1]}");

        foreach (WorkflowNode cropGuidesNode in WorkflowUtils.NodesOfType(workflow, "LTXVCropGuides"))
        {
            JArray cropLatentSource = WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "latent");
            WorkflowNode cropLatentSourceNode = WorkflowAssertions.RequireNodeById(workflow, $"{cropLatentSource[0]}");
            Assert.Contains($"{cropLatentSourceNode.Node["class_type"]}", new[] { "SwarmKSampler", "KSamplerAdvanced" });
        }
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
        WorkflowNode rootBatchNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(rootBatchNode.Node, "image"),
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
    public void Clip_shaped_json_without_stages_does_not_run_additional_stages()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeClip(width: 768, height: 448)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
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
        Assert.Equal(2, samplers.Count);
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
