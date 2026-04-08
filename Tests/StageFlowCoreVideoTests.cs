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
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootStageWidth, 768);
        input.Set(VideoStagesExtension.RootStageHeight, 448);
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
            MakeStage(models.VideoModel.Name, "Generated", upscale: 2.0, upscaleMethod: "pixel-bicubic", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootStageWidth, 960);
        input.Set(VideoStagesExtension.RootStageHeight, 544);
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
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootStageWidth, 768);
        input.Set(VideoStagesExtension.RootStageHeight, 448);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode emptyLatentNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "EmptyLTXVLatentVideo"));
        Assert.Equal(768, emptyLatentNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(448, emptyLatentNode.Node["inputs"]?.Value<int>("height"));
    }

    [Fact]
    public void Root_stage_resolution_updates_native_ltxv2_empty_latent_dimensions_even_when_additional_stages_disabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        input.Set(VideoStagesExtension.RootStageWidth, 768);
        input.Set(VideoStagesExtension.RootStageHeight, 448);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode emptyLatentNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "EmptyLTXVLatentVideo"));
        Assert.Equal(768, emptyLatentNode.Node["inputs"]?.Value<int>("width"));
        Assert.Equal(448, emptyLatentNode.Node["inputs"]?.Value<int>("height"));
    }

    [Fact]
    public void Formatted_empty_stage_array_does_not_rewrite_native_ltxv2_root_guide_chain()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[ ]");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));
    }

    [Fact]
    public void Native_ltxv2_root_output_crops_guides_before_final_save()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode finalVideoDecode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "VAEDecodeTiled"));
        WorkflowNode cropGuidesNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(finalVideoDecode.Node, "samples")[0]}");
        Assert.Equal("LTXVCropGuides", $"{cropGuidesNode.Node["class_type"]}");

        WorkflowNode separateNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "latent")[0]}");
        Assert.Equal("LTXVSeparateAVLatent", $"{separateNode.Node["class_type"]}");

        WorkflowNode finalSampler = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .Last();
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "positive"),
            WorkflowAssertions.RequireConnectionInput(finalSampler.Node, "positive")));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "negative"),
            WorkflowAssertions.RequireConnectionInput(finalSampler.Node, "negative")));
    }

    [Fact]
    public void Root_guide_last_frame_reference_edit_stage_adds_resized_ltxv2_last_frame_guide()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideLastFrameReference, "edit0");
        input.Set(VideoStagesExtension.RootStageWidth, 768);
        input.Set(VideoStagesExtension.RootStageHeight, 448);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditImage(0));

        List<WorkflowNode> conditioningNodes = WorkflowUtils.NodesOfType(workflow, "LTXVConditioning")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode rootConditioning = conditioningNodes[0];
        WorkflowNode stageConditioning = conditioningNodes[^1];
        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode rootFirstGuideNode = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        WorkflowNode addGuideNode = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootFirstGuideNode.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
        Assert.Contains(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(stageConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
        Assert.Equal(3, addGuideNodes.Count);

        WorkflowNode preprocessNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(addGuideNode.Node, "image")[0]}");
        Assert.Equal("LTXVPreprocess", $"{preprocessNode.Node["class_type"]}");
        Assert.Equal(18, preprocessNode.Node["inputs"]?.Value<int>("img_compression"));

        WorkflowNode resizeNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image")[0]}");
        Assert.Equal("ResizeImageMaskNode", $"{resizeNode.Node["class_type"]}");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(resizeNode.Node, "input"),
            new JArray("60", 0)));
        Assert.Equal("scale dimensions", $"{resizeNode.Node["inputs"]?["resize_type"]}");
        Assert.Equal(768, resizeNode.Node["inputs"]?.Value<int>("resize_type.width"));
        Assert.Equal(448, resizeNode.Node["inputs"]?.Value<int>("resize_type.height"));
        Assert.Equal("center", $"{resizeNode.Node["inputs"]?["resize_type.crop"]}");
        Assert.Equal("nearest-exact", $"{resizeNode.Node["inputs"]?["scale_method"]}");

        Assert.Equal(RootVideoStageResizer.LastFrameGuideFrameIndex, addGuideNode.Node["inputs"]?.Value<int>("frame_idx"));
        Assert.Equal(0.7, addGuideNode.Node["inputs"]?.Value<double>("strength"));
        Assert.Contains(
            WorkflowUtils.FindInputConnections(workflow, new JArray(addGuideNode.Id, 0)),
            connection => connection.InputName == "positive");
        Assert.Contains(
            WorkflowUtils.FindInputConnections(workflow, new JArray(addGuideNode.Id, 1)),
            connection => connection.InputName == "negative");
    }

    [Fact]
    public void Root_guide_last_frame_reference_edit_stage_applies_even_when_additional_stages_disabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        input.Set(VideoStagesExtension.RootGuideLastFrameReference, "edit0");
        input.Set(VideoStagesExtension.RootStageWidth, 768);
        input.Set(VideoStagesExtension.RootStageHeight, 448);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditImage(0));

        WorkflowNode resizeNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ResizeImageMaskNode"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "input"),
                new JArray("60", 0)));
        Assert.Equal("scale dimensions", $"{resizeNode.Node["inputs"]?["resize_type"]}");
        Assert.Equal(768, resizeNode.Node["inputs"]?.Value<int>("resize_type.width"));
        Assert.Equal(448, resizeNode.Node["inputs"]?.Value<int>("resize_type.height"));
        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);
        Assert.Contains(addGuideNodes, node => node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        Assert.Contains(addGuideNodes, node => node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
    }

    [Fact]
    public void Root_guide_last_frame_reference_runs_on_all_ltx_stages_after_additional_video_stages()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(
                models.VideoModel.Name,
                "Refiner",
                upscale: 1.5,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
                steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideImageReference, "Refiner");
        input.Set(VideoStagesExtension.RootGuideLastFrameReference, "edit0");
        input.Set(VideoStagesExtension.RootStageWidth, 384);
        input.Set(VideoStagesExtension.RootStageHeight, 640);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditImage(0));

        List<WorkflowNode> conditioningNodes = WorkflowUtils.NodesOfType(workflow, "LTXVConditioning")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode rootConditioning = conditioningNodes[0];
        WorkflowNode finalConditioning = conditioningNodes[^1];
        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(4, addGuideNodes.Count);
        WorkflowNode rootFirstGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        WorkflowNode rootLastGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootFirstGuide.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
        WorkflowNode stageFirstGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(finalConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        WorkflowNode stageLastGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(stageFirstGuide.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);

        WorkflowNode rootPreprocessNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(rootLastGuide.Node, "image")[0]}");
        WorkflowNode rootResizeNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(rootPreprocessNode.Node, "image")[0]}");
        Assert.Equal("ResizeImageMaskNode", $"{rootResizeNode.Node["class_type"]}");
        Assert.Equal("scale dimensions", $"{rootResizeNode.Node["inputs"]?["resize_type"]}");
        Assert.Equal(384, rootResizeNode.Node["inputs"]?.Value<int>("resize_type.width"));
        Assert.Equal(640, rootResizeNode.Node["inputs"]?.Value<int>("resize_type.height"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(rootResizeNode.Node, "input"),
            new JArray("60", 0)));

        WorkflowNode stagePreprocessNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(stageLastGuide.Node, "image")[0]}");
        WorkflowNode stageResizeNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(stagePreprocessNode.Node, "image")[0]}");
        Assert.Equal("ResizeImageMaskNode", $"{stageResizeNode.Node["class_type"]}");
        Assert.Equal("scale dimensions", $"{stageResizeNode.Node["inputs"]?["resize_type"]}");
        Assert.Equal(576, stageResizeNode.Node["inputs"]?.Value<int>("resize_type.width"));
        Assert.Equal(960, stageResizeNode.Node["inputs"]?.Value<int>("resize_type.height"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(stageResizeNode.Node, "input"),
            new JArray("60", 0)));

        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(stageFirstGuide.Node, "positive"),
            new JArray(finalConditioning.Id, 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(stageFirstGuide.Node, "negative"),
            new JArray(finalConditioning.Id, 1)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(rootLastGuide.Node, "latent"),
            new JArray(rootFirstGuide.Id, 2)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(stageLastGuide.Node, "latent"),
            new JArray(stageFirstGuide.Id, 2)));

        List<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode rootSampler = samplers[0];
        WorkflowNode finalSampler = samplers[^1];
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(rootSampler.Node, "positive"),
            new JArray(rootLastGuide.Id, 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(rootSampler.Node, "negative"),
            new JArray(rootLastGuide.Id, 1)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(finalSampler.Node, "positive"),
            new JArray(stageLastGuide.Id, 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(finalSampler.Node, "negative"),
            new JArray(stageLastGuide.Id, 1)));
    }

    [Fact]
    public void Root_guide_last_frame_reference_uses_later_stage_strength_for_ltx_stage_one_and_above()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(
                models.VideoModel.Name,
                "Refiner",
                upscale: 1.5,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
                steps: 10),
            MakeStage(
                models.VideoModel.Name,
                "Refiner",
                upscale: 1.5,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
                steps: 8)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideImageReference, "Refiner");
        input.Set(VideoStagesExtension.RootGuideLastFrameReference, "edit0");
        input.Set(VideoStagesExtension.RootStageWidth, 384);
        input.Set(VideoStagesExtension.RootStageHeight, 640);
        input.Set(VideoStagesExtension.LTXVImgToVideoInplaceStrength, 0.8);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditImage(0));

        List<WorkflowNode> conditioningNodes = WorkflowUtils.NodesOfType(workflow, "LTXVConditioning")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(3, conditioningNodes.Count);
        WorkflowNode rootConditioning = conditioningNodes[0];
        WorkflowNode stageZeroConditioning = conditioningNodes[1];
        WorkflowNode stageOneConditioning = conditioningNodes[2];

        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(6, addGuideNodes.Count);

        WorkflowNode rootFirstGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        WorkflowNode rootLastGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootFirstGuide.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
        WorkflowNode stageZeroFirstGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(stageZeroConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        WorkflowNode stageZeroLastGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(stageZeroFirstGuide.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
        WorkflowNode stageOneFirstGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(stageOneConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        WorkflowNode stageOneLastGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(stageOneFirstGuide.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);

        Assert.Equal(VideoStagesExtension.DefaultLTXVImgToVideoInplaceStrength, rootFirstGuide.Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(VideoStagesExtension.DefaultLTXVImgToVideoInplaceStrength, rootLastGuide.Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(VideoStagesExtension.DefaultLTXVImgToVideoInplaceStrength, stageZeroFirstGuide.Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(VideoStagesExtension.DefaultLTXVImgToVideoInplaceStrength, stageZeroLastGuide.Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(RootVideoStageResizer.AdditionalStageGuideStrength, stageOneFirstGuide.Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(RootVideoStageResizer.AdditionalStageGuideStrength, stageOneLastGuide.Node["inputs"]?.Value<double>("strength"));
    }

    [Fact]
    public void Root_stage_resolution_updates_native_ltxv2_audio_noise_mask_dimensions()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.Width, 768);
        input.Set(T2IParamTypes.Height, 1280);
        input.Set(VideoStagesExtension.RootStageWidth, 384);
        input.Set(VideoStagesExtension.RootStageHeight, 640);
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
    public void Root_guide_last_frame_reference_default_skips_native_ltxv2_last_frame_guide()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideLastFrameReference, "Default");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditImage(0));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "ResizeImageMaskNode"));
        WorkflowNode addGuideNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));
        Assert.Equal(RootVideoStageResizer.FirstFrameGuideFrameIndex, addGuideNode.Node["inputs"]?.Value<int>("frame_idx"));
    }

    [Fact]
    public void Root_guide_last_frame_reference_default_reuses_native_video_end_frame_for_additional_ltx_stages()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideLastFrameReference, "Default");
        input.Set(T2IParamTypes.VideoEndFrame, BuildUploadedVideoEndFrame());
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps());

        List<WorkflowNode> conditioningNodes = WorkflowUtils.NodesOfType(workflow, "LTXVConditioning")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode rootConditioning = conditioningNodes[0];
        WorkflowNode stageConditioning = conditioningNodes[^1];
        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode rootFirstGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.FirstFrameGuideFrameIndex);
        WorkflowNode rootLastGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(rootFirstGuide.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
        WorkflowNode stageLastGuide = Assert.Single(
            addGuideNodes,
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "positive"),
                new JArray(stageConditioning.Id, 0))
                && node.Node["inputs"]?.Value<int>("frame_idx") == RootVideoStageResizer.LastFrameGuideFrameIndex);
        Assert.Equal(3, addGuideNodes.Count);

        WorkflowNode rootLoadNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(rootLastGuide.Node, "image")[0]}");
        Assert.Equal("LoadImage", $"{rootLoadNode.Node["class_type"]}");
        Assert.Equal("${videoendframe}", $"{rootLoadNode.Node["inputs"]?["image"]}");

        WorkflowNode preprocessNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(stageLastGuide.Node, "image")[0]}");
        Assert.Equal("LTXVPreprocess", $"{preprocessNode.Node["class_type"]}");

        WorkflowNode resizeNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image")[0]}");
        Assert.Equal("ResizeImageMaskNode", $"{resizeNode.Node["class_type"]}");

        WorkflowNode stageLoadNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(resizeNode.Node, "input")[0]}");
        Assert.Equal("LoadImage", $"{stageLoadNode.Node["class_type"]}");
        Assert.Equal("${videoendframe}", $"{stageLoadNode.Node["inputs"]?["image"]}");
    }

    [Fact]
    public void Root_stage_resolution_updates_native_wan22_latent_dimensions()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootStageWidth, 832);
        input.Set(VideoStagesExtension.RootStageHeight, 480);
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
            MakeStage(models.VideoModel.Name, "Generated", steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(VideoStagesExtension.RootGuideImageReference, "edit0");
        input.Set(VideoStagesExtension.RootStageWidth, 768);
        input.Set(VideoStagesExtension.RootStageHeight, 448);
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
    public void Root_guide_image_reference_edit_stage_applies_even_when_additional_stages_disabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        input.Set(VideoStagesExtension.RootGuideImageReference, "edit0");
        input.Set(VideoStagesExtension.RootStageWidth, 768);
        input.Set(VideoStagesExtension.RootStageHeight, 448);
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
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(scaleNode.Id, 0)));
    }

    [Fact]
    public void Root_guide_image_reference_edit_stage_reuses_existing_decode_when_published_ref_is_latent()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        input.Set(VideoStagesExtension.RootGuideImageReference, "edit0");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPublishedBase2EditLatentRef(0));

        WorkflowNode scaleNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("61", 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(scaleNode.Node, "image"),
            new JArray("61", 0)));

        List<WorkflowInputConnection> decodeConsumers = WorkflowUtils.FindInputConnections(workflow, new JArray("60", 0))
            .Where(connection =>
            {
                string classType = $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}";
                return classType == "VAEDecode" || classType == "VAEDecodeTiled";
            })
            .ToList();
        WorkflowInputConnection decodeConsumer = Assert.Single(decodeConsumers);
        Assert.Equal("61", decodeConsumer.NodeId);
    }

    [Fact]
    public void Root_guide_image_reference_refiner_applies_even_when_additional_stages_disabled()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        input.Set(VideoStagesExtension.RootGuideImageReference, "Refiner");
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithEditedCurrentImage());

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
        Assert.DoesNotContain(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray("70", 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imageFromBatch.Node, "image"),
            new JArray(scaleNode.Id, 0)));
    }

    [Fact]
    public void Root_guide_image_reference_base_does_not_reuse_downstream_refiner_decode()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, "[]", enableVideoStages: false);
        input.Set(VideoStagesExtension.RootGuideImageReference, "Base");
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithLatentBaseCaptureAndDownstreamRefinerDecode());

        StageRefStore store = new(generator);
        WorkflowNode scaleNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ImageScale"));
        JArray guideImagePath = WorkflowAssertions.RequireConnectionInput(scaleNode.Node, "image");
        AssertGuideReferenceResolvesToPreprocessInput(workflow, guideImagePath, store.Base);
        Assert.False(JToken.DeepEquals(guideImagePath, new JArray("8", 0)));
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
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));
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
