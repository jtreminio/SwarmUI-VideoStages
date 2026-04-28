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

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithVideoControlNet(T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedCoreVideoControlNetBranchStep(controlNetModel), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedCoreVideoDiffPatchControlNetBranchStep(controlNetModel), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoControlNetBranchStep(T2IModel controlNetModel) =>
        new(g =>
        {
            string videoLoad = g.CreateNode("SwarmLoadVideoB64", new JObject()
            {
                ["video_base64"] = "unit-test-video"
            }, id: "300", idMandatory: false);
            string videoComponents = g.CreateNode("GetVideoComponents", new JObject()
            {
                ["video"] = new JArray(videoLoad, 0)
            }, id: "301", idMandatory: false);
            string scaled = g.CreateNode("ImageScale", new JObject()
            {
                ["image"] = new JArray(videoComponents, 0),
                ["width"] = 512,
                ["height"] = 512,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            }, id: "302", idMandatory: false);
            string preprocessor = g.CreateNode("UnitTestPreprocessor", new JObject()
            {
                ["image"] = new JArray(scaled, 0)
            }, id: "303", idMandatory: false);
            string controlNetLoader = g.CreateNode("ControlNetLoader", new JObject()
            {
                ["control_net_name"] = controlNetModel.ToString(g.ModelFolderFormat)
            }, id: "304", idMandatory: false);
            string positive = g.CreateNode("UnitTest_PositiveCond", new JObject(), id: "305", idMandatory: false);
            string negative = g.CreateNode("UnitTest_NegativeCond", new JObject(), id: "306", idMandatory: false);
            string controlApply = g.CreateNode("ControlNetApplyAdvanced", new JObject()
            {
                ["positive"] = new JArray(positive, 0),
                ["negative"] = new JArray(negative, 0),
                ["control_net"] = new JArray(controlNetLoader, 0),
                ["image"] = new JArray(preprocessor, 0),
                ["strength"] = 0.8,
                ["start_percent"] = 0,
                ["end_percent"] = 1
            }, id: "307", idMandatory: false);
            g.FinalPrompt = new JArray(controlApply, 0);
            g.FinalNegativePrompt = new JArray(controlApply, 1);
        }, -6.1);

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoDiffPatchControlNetBranchStep(T2IModel controlNetModel) =>
        new(g =>
        {
            string videoLoad = g.CreateNode("SwarmLoadVideoB64", new JObject()
            {
                ["video_base64"] = "unit-test-video"
            }, id: "300", idMandatory: false);
            string videoComponents = g.CreateNode("GetVideoComponents", new JObject()
            {
                ["video"] = new JArray(videoLoad, 0)
            }, id: "301", idMandatory: false);
            string scaled = g.CreateNode("ImageScale", new JObject()
            {
                ["image"] = new JArray(videoComponents, 0),
                ["width"] = 512,
                ["height"] = 512,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            }, id: "302", idMandatory: false);
            string preprocessor = g.CreateNode("UnitTestPreprocessor", new JObject()
            {
                ["image"] = new JArray(scaled, 0)
            }, id: "303", idMandatory: false);
            string modelPatchLoader = g.CreateNode("ModelPatchLoader", new JObject()
            {
                ["name"] = controlNetModel.ToString(g.ModelFolderFormat)
            }, id: "304", idMandatory: false);
            string diffPatch = g.CreateNode("QwenImageDiffsynthControlnet", new JObject()
            {
                ["model"] = g.CurrentModel.Path,
                ["model_patch"] = new JArray(modelPatchLoader, 0),
                ["vae"] = g.CurrentVae.Path,
                ["image"] = new JArray(preprocessor, 0),
                ["strength"] = 0.8
            }, id: "307", idMandatory: false);
            g.CurrentModel = g.CurrentModel.WithPath(new JArray(diffPatch, 0));
        }, -6.1);

    private static void AssertCoreVideoControlNetScaleReplaced(JObject workflow)
    {
        WorkflowNode resizeNode = WorkflowAssertions.RequireNodeById(workflow, "302");
        Assert.Equal("ResizeImageMaskNode", $"{resizeNode.Node["class_type"]}");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(resizeNode.Node, "input"),
            new JArray("301", 0)));
        Assert.Equal("scale shorter dimension", $"{resizeNode.Node["inputs"]?["resize_type"]}");
        Assert.Equal(512, resizeNode.Node["inputs"]?.Value<int>("resize_type.shorter_size"));
        Assert.Equal("lanczos", $"{resizeNode.Node["inputs"]?["scale_method"]}");
        Assert.Null(resizeNode.Node["inputs"]?["width"]);
        Assert.Null(resizeNode.Node["inputs"]?["height"]);
        Assert.Null(resizeNode.Node["inputs"]?["upscale_method"]);
        Assert.Null(resizeNode.Node["inputs"]?["crop"]);

        WorkflowNode multipleResizeNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ResizeImageMaskNode"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "input"),
                new JArray("302", 0)));
        Assert.Equal("scale to multiple", $"{multipleResizeNode.Node["inputs"]?["resize_type"]}");
        Assert.Equal(64, multipleResizeNode.Node["inputs"]?.Value<int>("resize_type.multiple"));
        Assert.Equal("lanczos", $"{multipleResizeNode.Node["inputs"]?["scale_method"]}");

        WorkflowNode preprocessorNode = WorkflowAssertions.RequireNodeById(workflow, "303");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(preprocessorNode.Node, "image"),
            new JArray(multipleResizeNode.Id, 0)));
    }

    private static WorkflowNode AssertCropGuidesLatentUsesVideoTensor(JObject workflow, WorkflowNode cropGuidesNode)
    {
        JArray cropLatentSource = WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "latent");
        WorkflowNode cropLatentSourceNode = WorkflowAssertions.RequireNodeById(workflow, $"{cropLatentSource[0]}");
        string cropLatentSourceType = $"{cropLatentSourceNode.Node["class_type"]}";
        if (cropLatentSourceType is "SwarmKSampler" or "KSamplerAdvanced")
        {
            return cropLatentSourceNode;
        }

        Assert.Equal("LTXVSeparateAVLatent", cropLatentSourceType);
        Assert.Equal("0", $"{cropLatentSource[1]}");
        JArray avLatentSource = WorkflowAssertions.RequireConnectionInput(cropLatentSourceNode.Node, "av_latent");
        WorkflowNode samplerNode = WorkflowAssertions.RequireNodeById(workflow, $"{avLatentSource[0]}");
        Assert.Contains($"{samplerNode.Node["class_type"]}", new[] { "SwarmKSampler", "KSamplerAdvanced" });
        return samplerNode;
    }

    [Fact]
    public void Configured_video_stages_without_native_image_to_video_toggle_run_from_stage_model()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated"));

        T2IParamInput input = BuildInput(models.BaseModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNoopSteps());

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Single(samplers);
        Assert.Single(WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS"));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Null(input.Get(T2IParamTypes.VideoModel, null));
    }

    [Fact]
    public void Single_stage_on_native_video_workflow_adds_one_sampler_and_reuses_final_save()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

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
    public void Clip_controlnet_source_applies_selected_swarm_controlnet_slot_to_stage_conditioning()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };

        JObject clip = MakeClip(
            384,
            640,
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                upscale: 1.125,
                upscaleMethod: "latentmodel-unit-upscaler.safetensors",
                steps: 10));
        clip["ControlNetSource"] = Constants.ControlNetSourceTwo;
        string stagesJson = new JArray(clip).ToString();
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.Controlnets[1].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[1].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[1], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        WorkflowNode preprocessor = Assert.Single(WorkflowUtils.NodesOfType(workflow, "UnitTestPreprocessor"));
        WorkflowNode controlApply = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ControlNetApplyAdvanced"));
        WorkflowNode controlLoader = Assert.Single(WorkflowUtils.NodesOfType(workflow, "ControlNetLoader"));
        Assert.Equal("UnitTest_ControlNet.safetensors", $"{controlLoader.Node["inputs"]?["control_net_name"]}");
        Assert.Equal(0.8, controlApply.Node["inputs"]?.Value<double>("strength"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(controlApply.Node, "image"),
            new JArray(preprocessor.Id, 0)));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessor.Node, "image"),
            new JArray("12", 0)));
    }

    [Fact]
    public void Clip_controlnet_video_source_reuses_core_preprocessor_for_stage_conditioning()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/controlnet",
                Name = "Unit ControlNet",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };

        JObject clip = MakeClip(512, 512, MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["ControlNetSource"] = Constants.ControlNetSourceTwo;
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[1].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[1].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[1], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoControlNet(controlNetModel));

        AssertCoreVideoControlNetScaleReplaced(workflow);
        WorkflowNode preprocessor = Assert.Single(WorkflowUtils.NodesOfType(workflow, "UnitTestPreprocessor"));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(WorkflowAssertions.RequireConnectionInput(node.Node, "image"), new JArray(preprocessor.Id, 0)));
        Assert.Equal(0, imageFromBatch.Node["inputs"]?.Value<int>("batch_index"));
        Assert.Equal(1, imageFromBatch.Node["inputs"]?.Value<int>("length"));

        List<WorkflowNode> controlApplies = WorkflowUtils.NodesOfType(workflow, "ControlNetApplyAdvanced")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, controlApplies.Count);
        Assert.Contains(controlApplies, node => JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
            new JArray(imageFromBatch.Id, 0)));
        Assert.Contains(controlApplies, node => JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
            new JArray(preprocessor.Id, 0)));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessor.Node, "image"),
            new JArray("301", 0)));
    }

    [Fact]
    public void Clip_controlnet_diffpatch_video_source_reuses_core_preprocessor_for_stage_model_patch()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/control-diffpatch",
                Name = "Unit ControlNet DiffPatch",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };

        JObject clip = MakeClip(512, 512, MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel));

        AssertCoreVideoControlNetScaleReplaced(workflow);
        WorkflowNode preprocessor = Assert.Single(WorkflowUtils.NodesOfType(workflow, "UnitTestPreprocessor"));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(WorkflowAssertions.RequireConnectionInput(node.Node, "image"), new JArray(preprocessor.Id, 0)));
        Assert.Equal(0, imageFromBatch.Node["inputs"]?.Value<int>("batch_index"));
        Assert.Equal(1, imageFromBatch.Node["inputs"]?.Value<int>("length"));

        List<WorkflowNode> diffPatchNodes = WorkflowUtils.NodesOfType(workflow, "QwenImageDiffsynthControlnet")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, diffPatchNodes.Count);
        Assert.Contains(diffPatchNodes, node => JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
            new JArray(imageFromBatch.Id, 0)));
        Assert.Contains(diffPatchNodes, node => JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
            new JArray(preprocessor.Id, 0)));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessor.Node, "image"),
            new JArray("301", 0)));
    }

    [Fact]
    public void Clip_controlnet_diffpatch_video_source_works_without_root_video_model_param()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/control-diffpatch",
                Name = "Unit ControlNet DiffPatch",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };

        JObject clip = MakeClip(512, 512, MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        T2IParamInput input = BuildInput(models.BaseModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel));

        AssertCoreVideoControlNetScaleReplaced(workflow);
        WorkflowNode preprocessor = Assert.Single(WorkflowUtils.NodesOfType(workflow, "UnitTestPreprocessor"));
        WorkflowNode imageFromBatch = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(WorkflowAssertions.RequireConnectionInput(node.Node, "image"), new JArray(preprocessor.Id, 0)));
        Assert.Equal(0, imageFromBatch.Node["inputs"]?.Value<int>("batch_index"));
        Assert.Equal(1, imageFromBatch.Node["inputs"]?.Value<int>("length"));
    }

    [Fact]
    public void Ltx_ic_lora_controlnet_source_adds_video_guide_without_stage_diffpatch()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/control-diffpatch",
                Name = "Unit ControlNet DiffPatch",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_ControlNetLora.safetensors", "UnitTest_ControlNetLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject clip = MakeClip(512, 512, MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel));

        AssertCoreVideoControlNetScaleReplaced(workflow);
        WorkflowNode icLora = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXICLoRALoaderModelOnly"));
        WorkflowNode preprocessor = Assert.Single(WorkflowUtils.NodesOfType(workflow, "UnitTestPreprocessor"));
        WorkflowNode firstFrame = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(WorkflowAssertions.RequireConnectionInput(node.Node, "image"), new JArray(preprocessor.Id, 0))
                && node.Node["inputs"]?.Value<int>("length") == 1);
        Assert.Equal(0, firstFrame.Node["inputs"]?.Value<int>("batch_index"));
        WorkflowNode videoGuideFrames = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => JToken.DeepEquals(WorkflowAssertions.RequireConnectionInput(node.Node, "image"), new JArray(preprocessor.Id, 0))
                && node.Node["inputs"]?.Value<int>("length") == 16);
        Assert.Equal(0, videoGuideFrames.Node["inputs"]?.Value<int>("batch_index"));

        WorkflowNode coreDiffPatch = Assert.Single(WorkflowUtils.NodesOfType(workflow, "QwenImageDiffsynthControlnet"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(coreDiffPatch.Node, "image"),
            new JArray(firstFrame.Id, 0)));
        Assert.False(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(coreDiffPatch.Node, "model"),
            new JArray(icLora.Id, 0)));

        WorkflowNode icGuide = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXAddVideoICLoRAGuide"));
        Assert.Equal(0, icGuide.Node["inputs"]?.Value<int>("frame_idx"));
        Assert.Equal(0.8, icGuide.Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(2.0, icGuide.Node["inputs"]?.Value<double>("latent_downscale_factor"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(icGuide.Node, "image"),
            new JArray(videoGuideFrames.Id, 0)));
        Assert.Contains(
            WorkflowUtils.FindInputConnections(workflow, new JArray(icGuide.Id, 2)),
            connection => connection.InputName == "video_latent"
                && $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}" == "LTXVConcatAVLatent");
    }

    [Fact]
    public void Ltx_ic_lora_controlnet_source_uses_stage_controlnet_strength()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/control-diffpatch",
                Name = "Unit ControlNet DiffPatch",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_ControlNetLora.safetensors", "UnitTest_ControlNetLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject stageA = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject stageB = MakeStage(models.VideoModel.Name, "PreviousStage", steps: 10);
        stageA["ControlNetStrength"] = 0.7;
        stageB["ControlNetStrength"] = 0.3;
        JObject clip = MakeClip(512, 512, stageA, stageB);
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel));

        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXAddVideoICLoRAGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);
        Assert.Equal(0.7, addGuideNodes[0].Node["inputs"]?.Value<double>("strength"));
        Assert.Equal(0.3, addGuideNodes[1].Node["inputs"]?.Value<double>("strength"));
    }

    [Fact]
    public void Ltx_ic_lora_controlnet_source_zero_strength_skips_guide_and_crop()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler controlNetHandler = new() { ModelType = "ControlNet" };
        T2IModel controlNetModel = new(controlNetHandler, "/tmp", "/tmp/UnitTest_ControlNet.safetensors", "UnitTest_ControlNet.safetensors")
        {
            ModelClass = new T2IModelClass()
            {
                ID = "unit/control-diffpatch",
                Name = "Unit ControlNet DiffPatch",
                CompatClass = models.VideoModel.ModelClass.CompatClass
            }
        };
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel loraModel = new(loraHandler, "/tmp", "/tmp/UnitTest_ControlNetLora.safetensors", "UnitTest_ControlNetLora.safetensors");
        loraHandler.Models[loraModel.Name] = loraModel;

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["ControlNetStrength"] = 0.0;
        JObject clip = MakeClip(512, 512, stage);
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXAddVideoICLoRAGuide"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVCropGuides"));
    }

    [Fact]
    public void Two_defaulted_ltx_stages_use_root_image_refs_and_save_second_stage_audio()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage0 = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        JObject stage1 = MakeStage(models.VideoModel.Name, "PreviousStage", steps: 8);
        stage0.Remove("ImageReference");
        stage1.Remove("ImageReference");
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 1024, height: 1024, refs: [], stage0, stage1)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        StageRefStore store = new(generator);
        Assert.NotNull(store.Refiner);

        List<WorkflowNode> imgToVideoNodes = WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, imgToVideoNodes.Count);
        WorkflowNode preprocessNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        foreach (WorkflowNode imgToVideo in imgToVideoNodes)
        {
            AssertGuideReferenceResolvesToPreprocessInput(
                workflow,
                WorkflowAssertions.RequireConnectionInput(imgToVideo.Node, "image"),
                store.Refiner);
            Assert.True(JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(imgToVideo.Node, "image"),
                new JArray(preprocessNode.Id, 0)));
        }

        WorkflowNode guideScale = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => node.Node["inputs"]?.Value<int>("width") == 1024
                && node.Node["inputs"]?.Value<int>("height") == 1024);
        Assert.Equal("center", $"{guideScale.Node["inputs"]?["crop"]}");

        List<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, samplers.Count);
        WorkflowNode saveNode = Assert.Single(WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS"));
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "images"),
            new JArray(samplers[1].Id, 0)));

        JArray saveAudio = WorkflowAssertions.RequireConnectionInput(saveNode.Node, "audio");
        WorkflowNode finalAudioDecode = WorkflowAssertions.RequireNodeById(workflow, $"{saveAudio[0]}");
        Assert.Equal("LTXVAudioVAEDecode", $"{finalAudioDecode.Node["class_type"]}");
        JArray finalAudioSamples = WorkflowAssertions.RequireConnectionInput(finalAudioDecode.Node, "samples");
        WorkflowNode finalSeparate = WorkflowAssertions.RequireNodeById(workflow, $"{finalAudioSamples[0]}");
        Assert.Equal("LTXVSeparateAVLatent", $"{finalSeparate.Node["class_type"]}");
        JArray finalSeparateAvLatent = WorkflowAssertions.RequireConnectionInput(finalSeparate.Node, "av_latent");
        Assert.True(
            OutputTracesBackToSource(workflow, finalSeparateAvLatent, new JArray(samplers[1].Id, 0)),
            $"Expected save audio to decode stage 1 latent [{samplers[1].Id}, 0], but audio decode samples [{finalAudioSamples[0]}, {finalAudioSamples[1]}] came from separate {finalSeparate.Id} with av_latent [{finalSeparateAvLatent[0]}, {finalSeparateAvLatent[1]}].");
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

        Assert.Equal(1280, generator.CurrentMedia.Width);
        Assert.Equal(1024, generator.CurrentMedia.Height);

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
        Assert.Equal(721, emptyLatentNode.Node["inputs"]?.Value<int>("length"));
        Assert.All(
            WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"),
            node => Assert.Equal(721, node.Node["inputs"]?.Value<int>("length")));
    }

    [Fact]
    public void Core_video_workflow_uses_clip_zero_as_root_ltx_stage()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10));

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
        JArray preprocessImageIn = WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image");
        AssertGuideReferenceResolvesToPreprocessInput(workflow, preprocessImageIn, store.Base);
        Assert.False(OutputTracesBackToSource(workflow, preprocessImageIn, new JArray(rootScaleNode.Id, 0)));

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
        AssertCropGuidesLatentUsesVideoTensor(workflow, cropGuidesNode);
        JArray cropPositiveIn = WorkflowAssertions.RequireConnectionInput(cropGuidesNode.Node, "positive");
        Assert.Equal(addGuideNode.Id, $"{cropPositiveIn[0]}");

        WorkflowNode finalDecode = Assert.Single(
            WorkflowAssertions.NodesOfAnyType(workflow, "VAEDecode", "VAEDecodeTiled"),
            node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "samples"),
                new JArray(cropGuidesNode.Id, 2)));
        AssertLtxFinalDecodeUsesPlainVaeDecode(finalDecode);
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
    public void Chained_ltx_latent_model_upscale_uses_video_latent_directly()
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
        Assert.Equal("LTXVCropGuides", $"{upsamplerSource.Node["class_type"]}");
        Assert.Equal("2", $"{upsamplerSamples[1]}");

        foreach (WorkflowNode cropGuidesNode in WorkflowUtils.NodesOfType(workflow, "LTXVCropGuides"))
        {
            AssertCropGuidesLatentUsesVideoTensor(workflow, cropGuidesNode);
        }
    }

    [Fact]
    public void Chained_ltx_latent_model_upscales_preprocess_guides_at_stage_resolution()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                384,
                640,
                [MakeRef("Refiner", frame: 1)],
                MakeStage(models.VideoModel.Name, "Generated", steps: 8),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    upscale: 1.5,
                    upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
                    steps: 8),
                MakeStage(
                    models.VideoModel.Name,
                    "PreviousStage",
                    upscale: 1.5,
                    upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
                    steps: 8))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());

        Assert.Equal(2, WorkflowUtils.NodesOfType(workflow, "LTXVLatentUpsampler").Count);
        List<(int Width, int Height)> guideScaleSizes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .Select(node => WorkflowAssertions.RequireConnectionInput(node.Node, "image"))
            .Select(path => WorkflowAssertions.RequireNodeById(workflow, $"{path[0]}"))
            .Where(node => $"{node.Node["class_type"]}" == "ImageScale")
            .Select(node => (
                Width: node.Node["inputs"]!.Value<int>("width"),
                Height: node.Node["inputs"]!.Value<int>("height")))
            .OrderBy(size => size.Width)
            .ThenBy(size => size.Height)
            .ToList();

        Assert.Contains((384, 640), guideScaleSizes);
        Assert.Contains((576, 960), guideScaleSizes);
        Assert.Contains((864, 1440), guideScaleSizes);
        foreach (WorkflowNode scaleNode in WorkflowUtils.NodesOfType(workflow, "ImageScale"))
        {
            int? width = scaleNode.Node["inputs"]?.Value<int?>("width");
            int? height = scaleNode.Node["inputs"]?.Value<int?>("height");
            if ((width == 576 && height == 960) || (width == 864 && height == 1440))
            {
                JArray scaleInput = WorkflowAssertions.RequireConnectionInput(scaleNode.Node, "image");
                WorkflowNode scaleInputNode = WorkflowAssertions.RequireNodeById(workflow, $"{scaleInput[0]}");
                Assert.NotEqual("ImageScale", $"{scaleInputNode.Node["class_type"]}");
            }
        }
        Assert.Equal(864, generator.CurrentMedia.Width);
        Assert.Equal(1440, generator.CurrentMedia.Height);
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
    public void Text_to_video_root_model_without_video_model_replaces_core_stage_and_ignores_non_upload_refs()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 512,
                height: 512,
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 2)],
                MakeStage(models.VideoModel.Name, "Base", steps: 10)))
            .ToString();

        T2IParamInput input = BuildTextToVideoInput(models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildTextToVideoSteps(attachAudioToCurrentMedia: true));

        IReadOnlyList<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler");
        Assert.Single(samplers);
        Assert.False(workflow.ContainsKey("200"));
        Assert.False(workflow.ContainsKey("201"));
        Assert.False(workflow.ContainsKey("202"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);

        StageRefStore store = new(generator);
        Assert.NotNull(store.Generated);
    }

    [Fact]
    public void Text_to_video_chained_ltx_stages_without_upload_refs_reuse_av_latent_directly()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Base", steps: 8),
            MakeStage(models.VideoModel.Name, "Refiner", steps: 8),
            MakeStage(models.VideoModel.Name, "Generated", steps: 8));

        T2IParamInput input = BuildTextToVideoInput(models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildTextToVideoSteps(attachAudioToCurrentMedia: true));

        Assert.Equal(3, WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler").Count);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "ImageScale"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        Assert.Equal(3, WorkflowUtils.NodesOfType(workflow, "LTXVConcatAVLatent").Count);

        foreach (WorkflowNode concatNode in WorkflowUtils.NodesOfType(workflow, "LTXVConcatAVLatent").Skip(1))
        {
            WorkflowNode videoSource = WorkflowAssertions.RequireNodeById(
                workflow,
                $"{WorkflowAssertions.RequireConnectionInput(concatNode.Node, "video_latent")[0]}");
            Assert.Equal("LTXVSeparateAVLatent", $"{videoSource.Node["class_type"]}");
        }
    }

    [Fact]
    public void Two_stages_on_native_video_workflow_add_two_samplers_and_keep_single_final_save()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 14, cfgScale: 6.0, sampler: "dpmpp_2m", scheduler: "karras"));

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

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
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
