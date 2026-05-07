using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
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
    private static WorkflowGenerator.WorkflowGenStep SeedRootRawAudioAttachmentStep() =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            UnknownNode rawAudio = bridge.AddStub("UnitTest_RawAudio", "50").WithOutputs("AUDIO");
            g.CurrentMedia.AttachedAudio = rawAudio.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIO);
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

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNetFirstFrame(T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedCoreVideoDiffPatchControlNetBranchStep(controlNetModel, true), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNetNoPreprocessor(T2IModel controlNetModel) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedCoreVideoDiffPatchControlNetBranchStepNoPreprocessor(controlNetModel), SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoControlNetBranchStep(T2IModel controlNetModel) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);

            SwarmLoadVideoB64Node videoLoad = new();
            videoLoad.VideoBase64.Set("unit-test-video");
            bridge.AddNode(videoLoad, "300");

            GetVideoComponentsNode videoComponents = new();
            videoComponents.Video.ConnectTo(videoLoad.VIDEO);
            bridge.AddNode(videoComponents, "301");

            ImageScaleNode scaled = new();
            scaled.Image.ConnectTo(videoComponents.Images);
            scaled.Width.Set(512L);
            scaled.Height.Set(512L);
            scaled.UpscaleMethod.Set("lanczos");
            scaled.Crop.Set("disabled");
            bridge.AddNode(scaled, "302");

            UnknownNode preprocessor = bridge.AddStub("UnitTestPreprocessor", "303").WithOutputs("IMAGE");
            preprocessor.GetInput("image").ConnectToUntyped(scaled.IMAGE);

            ResizeImageMaskNodeNode resize = new()
            {
                ExtraInputs = new JObject { ["resize_type.multiple"] = 8 }
            };
            resize.Input.ConnectToUntyped(preprocessor.GetOutput(0));
            resize.ResizeType.Set("scale to multiple");
            resize.ScaleMethod.Set("lanczos");
            bridge.AddNode(resize, "304");

            ControlNetLoaderNode controlNetLoader = new();
            controlNetLoader.ControlNetName.Set(controlNetModel.ToString(g.ModelFolderFormat));
            bridge.AddNode(controlNetLoader, "305");

            UnknownNode positive = bridge.AddStub("UnitTest_PositiveCond", "306").WithOutputs("CONDITIONING");
            UnknownNode negative = bridge.AddStub("UnitTest_NegativeCond", "307").WithOutputs("CONDITIONING");

            ControlNetApplyAdvancedNode controlApply = new();
            controlApply.PositiveInput.ConnectToUntyped(positive.GetOutput(0));
            controlApply.NegativeInput.ConnectToUntyped(negative.GetOutput(0));
            controlApply.ControlNet.ConnectTo(controlNetLoader.CONTROLNET);
            controlApply.Image.ConnectToUntyped(resize.Resized);
            controlApply.Strength.Set(0.8);
            controlApply.StartPercent.Set(0.0);
            controlApply.EndPercent.Set(1.0);
            bridge.AddNode(controlApply, "308");

            g.FinalPrompt = new JArray("308", 0);
            g.FinalNegativePrompt = new JArray("308", 1);
        }, -6.1);

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoDiffPatchControlNetBranchStep(
        T2IModel controlNetModel,
        bool useFirstFrameForCoreApply = false) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);

            SwarmLoadVideoB64Node videoLoad = new();
            videoLoad.VideoBase64.Set("unit-test-video");
            bridge.AddNode(videoLoad, "300");

            GetVideoComponentsNode videoComponents = new();
            videoComponents.Video.ConnectTo(videoLoad.VIDEO);
            bridge.AddNode(videoComponents, "301");

            ImageScaleNode scaled = new();
            scaled.Image.ConnectTo(videoComponents.Images);
            scaled.Width.Set(512L);
            scaled.Height.Set(512L);
            scaled.UpscaleMethod.Set("lanczos");
            scaled.Crop.Set("disabled");
            bridge.AddNode(scaled, "302");

            UnknownNode preprocessor = bridge.AddStub("UnitTestPreprocessor", "303").WithOutputs("IMAGE");
            preprocessor.GetInput("image").ConnectToUntyped(scaled.IMAGE);

            ResizeImageMaskNodeNode resize = new()
            {
                ExtraInputs = new JObject { ["resize_type.multiple"] = 8 }
            };
            resize.Input.ConnectToUntyped(preprocessor.GetOutput(0));
            resize.ResizeType.Set("scale to multiple");
            resize.ScaleMethod.Set("lanczos");
            bridge.AddNode(resize, "304");

            ModelPatchLoaderNode modelPatchLoader = new();
            modelPatchLoader.Name.Set(controlNetModel.ToString(g.ModelFolderFormat));
            bridge.AddNode(modelPatchLoader, "305");

            INodeOutput controlImageOutput = resize.Resized;
            string diffPatchId = "308";
            if (useFirstFrameForCoreApply)
            {
                ImageFromBatchNode firstFrame = new();
                firstFrame.Image.ConnectToUntyped(resize.Resized);
                firstFrame.BatchIndex.Set(0L);
                firstFrame.Length.Set(1L);
                bridge.AddNode(firstFrame, "308");
                controlImageOutput = firstFrame.IMAGE;
                diffPatchId = "309";
            }

            QwenImageDiffsynthControlnetNode diffPatch = new();
            diffPatch.Model.ConnectToUntyped(bridge.ResolvePath(g.CurrentModel.Path));
            diffPatch.ModelPatch.ConnectTo(modelPatchLoader.MODELPATCH);
            diffPatch.Vae.ConnectToUntyped(bridge.ResolvePath(g.CurrentVae.Path));
            diffPatch.Image.ConnectToUntyped(controlImageOutput);
            diffPatch.Strength.Set(0.8);
            bridge.AddNode(diffPatch, diffPatchId);

            g.CurrentModel = g.CurrentModel.WithPath(new JArray(diffPatchId, 0));
        }, -6.1);

    private static WorkflowGenerator.WorkflowGenStep SeedCoreVideoDiffPatchControlNetBranchStepNoPreprocessor(
        T2IModel controlNetModel) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);

            SwarmLoadVideoB64Node videoLoad = new();
            videoLoad.VideoBase64.Set("unit-test-video");
            bridge.AddNode(videoLoad, "300");

            GetVideoComponentsNode videoComponents = new();
            videoComponents.Video.ConnectTo(videoLoad.VIDEO);
            bridge.AddNode(videoComponents, "301");

            ImageScaleNode scaled = new();
            scaled.Image.ConnectTo(videoComponents.Images);
            scaled.Width.Set(512L);
            scaled.Height.Set(512L);
            scaled.UpscaleMethod.Set("lanczos");
            scaled.Crop.Set("disabled");
            bridge.AddNode(scaled, "302");

            ImageFromBatchNode firstFrame = new();
            firstFrame.Image.ConnectTo(scaled.IMAGE);
            firstFrame.BatchIndex.Set(0L);
            firstFrame.Length.Set(1L);
            bridge.AddNode(firstFrame, "303");

            ModelPatchLoaderNode modelPatchLoader = new();
            modelPatchLoader.Name.Set(controlNetModel.ToString(g.ModelFolderFormat));
            bridge.AddNode(modelPatchLoader, "304");

            QwenImageDiffsynthControlnetNode diffPatch = new();
            diffPatch.Model.ConnectToUntyped(bridge.ResolvePath(g.CurrentModel.Path));
            diffPatch.ModelPatch.ConnectTo(modelPatchLoader.MODELPATCH);
            diffPatch.Vae.ConnectToUntyped(bridge.ResolvePath(g.CurrentVae.Path));
            diffPatch.Image.ConnectTo(firstFrame.IMAGE);
            diffPatch.Strength.Set(0.8);
            bridge.AddNode(diffPatch, "305");

            g.CurrentModel = g.CurrentModel.WithPath(new JArray("305", 0));
        }, -6.1);

    private static void AssertCoreVideoControlNetResizeBumped(JObject workflow)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ImageScaleNode scaleNode = RequireTypedNode<ImageScaleNode>(bridge, "302");
        Assert.Equal("301", scaleNode.Image.Connection!.Node.Id);
        Assert.Equal(0, scaleNode.Image.Connection.SlotIndex);

        ComfyNode preprocessorNode = bridge.Graph.GetNode("303");
        Assert.NotNull(preprocessorNode);
        INodeInput preprocessorImage = preprocessorNode.FindInput("image");
        Assert.NotNull(preprocessorImage);
        Assert.Equal("302", preprocessorImage.Connection!.Node.Id);
        Assert.Equal(0, preprocessorImage.Connection.SlotIndex);

        ResizeImageMaskNodeNode resizeNode = RequireTypedNode<ResizeImageMaskNodeNode>(bridge, "304");
        Assert.Equal("303", resizeNode.Input.Connection!.Node.Id);
        Assert.Equal(0, resizeNode.Input.Connection.SlotIndex);
        Assert.Equal("scale to multiple", resizeNode.ResizeType.LiteralAsString());
        Assert.Equal(64, resizeNode.ExtraInputs?["resize_type.multiple"]?.Value<int>());
        Assert.Equal("lanczos", resizeNode.ScaleMethod.LiteralAsString());
    }

    private static ComfyNode AssertCropGuidesLatentUsesVideoTensor(JObject workflow, WorkflowNode cropGuidesNode)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        LTXVCropGuidesNode cropNode = RequireTypedNode<LTXVCropGuidesNode>(bridge, cropGuidesNode.Id);
        Assert.NotNull(cropNode.LatentInput.Connection);
        ComfyNode cropLatentSourceNode = cropNode.LatentInput.Connection.Node;
        if (cropLatentSourceNode is SwarmKSamplerNode)
        {
            return cropLatentSourceNode;
        }

        LTXVSeparateAVLatentNode separateNode = Assert.IsType<LTXVSeparateAVLatentNode>(cropLatentSourceNode);
        Assert.Equal(0, cropNode.LatentInput.Connection.SlotIndex);
        Assert.NotNull(separateNode.AvLatent.Connection);
        ComfyNode samplerNode = separateNode.AvLatent.Connection.Node;
        Assert.IsType<SwarmKSamplerNode>(samplerNode);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(SamplerNodesOrdered(bridge));
        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(saveNode.Images.Connection!),
            generator.CurrentMedia.Path));
    }

    [Fact]
    public void Clip_controlnet_source_ignored_for_non_ltx_video_stage_model()
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType("UnitTestPreprocessor"));
        Assert.Empty(bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ControlNetLoaderNode>());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertCoreVideoControlNetResizeBumped(workflow);
        ComfyNode preprocessor = Assert.Single(bridge.Graph.NodesOfType("UnitTestPreprocessor"));
        ComfyNode resize = bridge.Graph.GetNode("304");
        Assert.NotNull(resize);
        ImageFromBatchNode imageFromBatch = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == resize.Id && node.Image.Connection.SlotIndex == 0);
        Assert.Equal(0, imageFromBatch.BatchIndex.LiteralAsInt());
        Assert.Equal(1, imageFromBatch.Length.LiteralAsInt());

        List<ControlNetApplyAdvancedNode> controlApplies = bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        ControlNetApplyAdvancedNode stageControlApply = Assert.Single(controlApplies);
        Assert.Equal(imageFromBatch.Id, stageControlApply.Image.Connection!.Node.Id);
        Assert.Equal(0, stageControlApply.Image.Connection.SlotIndex);
        INodeInput preprocessorImage = preprocessor.FindInput("image");
        Assert.NotNull(preprocessorImage);
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(preprocessorImage.Connection!),
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertCoreVideoControlNetResizeBumped(workflow);
        ComfyNode preprocessor = Assert.Single(bridge.Graph.NodesOfType("UnitTestPreprocessor"));
        ComfyNode resize = bridge.Graph.GetNode("304");
        Assert.NotNull(resize);
        ImageFromBatchNode imageFromBatch = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == resize.Id && node.Image.Connection.SlotIndex == 0);
        Assert.Equal(0, imageFromBatch.BatchIndex.LiteralAsInt());
        Assert.Equal(1, imageFromBatch.Length.LiteralAsInt());

        List<QwenImageDiffsynthControlnetNode> diffPatchNodes = bridge.Graph.NodesOfType<QwenImageDiffsynthControlnetNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        QwenImageDiffsynthControlnetNode stageDiffPatch = Assert.Single(diffPatchNodes);
        Assert.Equal(imageFromBatch.Id, stageDiffPatch.Image.Connection!.Node.Id);
        Assert.Equal(0, stageDiffPatch.Image.Connection.SlotIndex);
        INodeInput preprocessorImage = preprocessor.FindInput("image");
        Assert.NotNull(preprocessorImage);
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(preprocessorImage.Connection!),
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertCoreVideoControlNetResizeBumped(workflow);
        Assert.Single(bridge.Graph.NodesOfType("UnitTestPreprocessor"));
        ComfyNode resize = bridge.Graph.GetNode("304");
        Assert.NotNull(resize);
        ImageFromBatchNode imageFromBatch = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == resize.Id && node.Image.Connection.SlotIndex == 0);
        Assert.Equal(0, imageFromBatch.BatchIndex.LiteralAsInt());
        Assert.Equal(1, imageFromBatch.Length.LiteralAsInt());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertCoreVideoControlNetResizeBumped(workflow);
        LTXICLoRALoaderModelOnlyNode icLora = Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        ComfyNode preprocessor = Assert.Single(bridge.Graph.NodesOfType("UnitTestPreprocessor"));
        ResizeImageMaskNodeNode scaleToMultipleNode = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>(),
            n => n.ResizeType.LiteralAsString() == "scale to multiple");
        Assert.Equal("304", scaleToMultipleNode.Id);
        Assert.Equal(64, scaleToMultipleNode.ExtraInputs?["resize_type.multiple"]?.Value<int>());
        Assert.Equal(preprocessor.Id, scaleToMultipleNode.Input.Connection!.Node.Id);
        Assert.Equal(0, scaleToMultipleNode.Input.Connection.SlotIndex);
        ImageFromBatchNode firstFrame = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == scaleToMultipleNode.Id && node.Image.Connection.SlotIndex == 0
                && node.Length.LiteralAsInt() == 1);
        Assert.Equal(0, firstFrame.BatchIndex.LiteralAsInt());
        ImageFromBatchNode videoGuideFrames = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == scaleToMultipleNode.Id && node.Image.Connection.SlotIndex == 0
                && node.Length.LiteralAsInt() == 16);
        Assert.Equal(0, videoGuideFrames.BatchIndex.LiteralAsInt());

        QwenImageDiffsynthControlnetNode coreDiffPatch = Assert.Single(bridge.Graph.NodesOfType<QwenImageDiffsynthControlnetNode>());
        Assert.Equal(firstFrame.Id, coreDiffPatch.Image.Connection!.Node.Id);
        Assert.Equal(0, coreDiffPatch.Image.Connection.SlotIndex);
        Assert.False(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(coreDiffPatch.Model.Connection!),
            new JArray(icLora.Id, 0)));

        LTXAddVideoICLoRAGuideNode icGuide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Equal(0, (int?)icGuide.FrameIdx.LiteralAsLong());
        Assert.Equal(0.8, icGuide.Strength.LiteralAsDouble());
        Assert.Equal(2.0, icGuide.LatentDownscaleFactor.LiteralAsDouble());
        Assert.Equal(videoGuideFrames.Id, icGuide.Image.Connection!.Node.Id);
        Assert.Equal(0, icGuide.Image.Connection.SlotIndex);
        Assert.Contains(
            bridge.Graph.FindInputsConnectedTo(icGuide.Latent),
            connection => connection.Input.Name == "video_latent" && connection.Node is LTXVConcatAVLatentNode);
    }

    [Fact]
    public void Clip_length_from_controlnet_uses_captured_video_batch_count_for_ltx_lengths()
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
        clip["ClipLengthFromControlNet"] = true;
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.NodesOfType("UnitTestPreprocessor"));
        ComfyNode resize = bridge.Graph.GetNode("304");
        Assert.NotNull(resize);
        GetImageSizeNode sizeNode = Assert.Single(bridge.Graph.NodesOfType<GetImageSizeNode>());
        JArray controlNetFrameCount = new(sizeNode.Id, 2);
        Assert.Equal(resize.Id, sizeNode.Image.Connection!.Node.Id);
        Assert.Equal(0, sizeNode.Image.Connection.SlotIndex);

        EmptyLTXVLatentVideoNode emptyLatent = Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Same(sizeNode.BatchSize, emptyLatent.Length.Connection);

        LTXVEmptyLatentAudioNode emptyAudio = Assert.Single(bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());
        Assert.Same(sizeNode.BatchSize, emptyAudio.FramesNumber.Connection);

        ResizeImageMaskNodeNode scaleToMultipleNode = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>(),
            n => n.ResizeType.LiteralAsString() == "scale to multiple");
        Assert.Equal(resize.Id, scaleToMultipleNode.Id);
        Assert.Equal(64, scaleToMultipleNode.ExtraInputs?["resize_type.multiple"]?.Value<int>());
        ImageFromBatchNode videoGuideFrames = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == scaleToMultipleNode.Id && node.Image.Connection.SlotIndex == 0
                && node.Length.Connection == sizeNode.BatchSize);
        Assert.Same(sizeNode.BatchSize, videoGuideFrames.Length.Connection);
    }

    [Fact]
    public void Chained_ltx_controlnet_length_latent_model_upscale_reuses_previous_stage_latent()
    {
        using SwarmUiTestContext _ = new();
        WorkflowTestHarness.VideoStagesSteps();
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

        JObject stageA = MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8, cfgScale: 1);
        JObject stageB = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            control: 0.5,
            upscale: 1.5,
            upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
            steps: 8,
            cfgScale: 1);
        stageA["ControlNetStrength"] = 1.0;
        stageA["refStrengths"] = new JArray(1.0);
        stageB["ControlNetStrength"] = 0.6;
        stageB["refStrengths"] = new JArray(0.8);
        JObject clip = MakeClipWithRefs(512, 768, [MakeRef("Base", frame: 1)], stageA, stageB);
        clip["ControlNetSource"] = Constants.ControlNetSourceOne;
        clip["ControlNetLora"] = "UnitTest_ControlNetLora";
        clip["ClipLengthFromControlNet"] = true;
        T2IParamInput input = BuildInput(models.BaseModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);
        input.Set(ComfyUIBackendExtension.ControlNetPreprocessorParams[0], "UnitTestPreprocessor");

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVLatentUpsamplerNode upsamplerNode = Assert.Single(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        Assert.IsType<LTXVCropGuidesNode>(upsamplerNode.Samples.Connection!.Node);
        Assert.Equal(2, upsamplerNode.Samples.Connection.SlotIndex);
    }

    [Fact]
    public void Ltx_ic_lora_controlnet_source_unwraps_core_first_frame_for_video_guide()
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
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNetFirstFrame(controlNetModel));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        AssertCoreVideoControlNetResizeBumped(workflow);
        ComfyNode preprocessor = Assert.Single(bridge.Graph.NodesOfType("UnitTestPreprocessor"));
        ComfyNode resize = bridge.Graph.GetNode("304");
        Assert.NotNull(resize);
        ImageFromBatchNode firstFrame = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == resize.Id && node.Image.Connection.SlotIndex == 0
                && node.Length.LiteralAsInt() == 1);
        ResizeImageMaskNodeNode scaleToMultipleNode = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>(),
            n => n.ResizeType.LiteralAsString() == "scale to multiple");
        Assert.Equal(resize.Id, scaleToMultipleNode.Id);
        Assert.Equal(preprocessor.Id, scaleToMultipleNode.Input.Connection!.Node.Id);
        Assert.Equal(0, scaleToMultipleNode.Input.Connection.SlotIndex);
        ImageFromBatchNode videoGuideFrames = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == scaleToMultipleNode.Id && node.Image.Connection.SlotIndex == 0
                && node.Length.LiteralAsInt() == 16);

        QwenImageDiffsynthControlnetNode coreDiffPatch = Assert.Single(bridge.Graph.NodesOfType<QwenImageDiffsynthControlnetNode>());
        Assert.Equal(firstFrame.Id, coreDiffPatch.Image.Connection!.Node.Id);
        Assert.Equal(0, coreDiffPatch.Image.Connection.SlotIndex);
        LTXAddVideoICLoRAGuideNode icGuide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Equal(videoGuideFrames.Id, icGuide.Image.Connection!.Node.Id);
        Assert.Equal(0, icGuide.Image.Connection.SlotIndex);
    }

    [Fact]
    public void Ltx_ic_lora_controlnet_source_inserts_resize_when_core_skipped_preprocessor()
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
        clip["ClipLengthFromControlNet"] = true;
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        input.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        input.Set(T2IParamTypes.Controlnets[0].Model, controlNetModel);

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNetNoPreprocessor(controlNetModel));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode imageScale = RequireTypedNode<ImageScaleNode>(bridge, "302");
        ImageFromBatchNode coreFirstFrame = RequireTypedNode<ImageFromBatchNode>(bridge, "303");
        Assert.Equal(1, coreFirstFrame.Length.LiteralAsInt());

        ResizeImageMaskNodeNode resize = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>(),
            n => n.ResizeType.LiteralAsString() == "scale to multiple");
        Assert.Equal(64, resize.ExtraInputs?["resize_type.multiple"]?.Value<int>());
        Assert.Equal(imageScale.Id, resize.Input.Connection!.Node.Id);
        Assert.Equal(0, resize.Input.Connection.SlotIndex);
        Assert.Equal(resize.Id, coreFirstFrame.Image.Connection!.Node.Id);
        Assert.Equal(0, coreFirstFrame.Image.Connection.SlotIndex);

        GetImageSizeNode sizeNode = Assert.Single(bridge.Graph.NodesOfType<GetImageSizeNode>());
        Assert.Equal(resize.Id, sizeNode.Image.Connection!.Node.Id);
        Assert.Equal(0, sizeNode.Image.Connection.SlotIndex);

        ImageFromBatchNode videoGuideFrames = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => node.Image.Connection?.Node.Id == resize.Id && node.Image.Connection.SlotIndex == 0
                && node.Length.Connection == sizeNode.BatchSize);
        LTXAddVideoICLoRAGuideNode icGuide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Equal(videoGuideFrames.Id, icGuide.Image.Connection!.Node.Id);
        Assert.Equal(0, icGuide.Image.Connection.SlotIndex);
    }

    [Fact]
    public void Ltx_ic_lora_controlnet_source_requires_ltxvideo_nodes()
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

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildCoreVideoWorkflowStepsWithVideoDiffPatchControlNet(controlNetModel),
                ComfyUIBackendExtension.FeaturesSupported
                    .Where(feature => feature != Constants.LtxVideoFeatureFlag)));
        Assert.Contains("ComfyUI-LTXVideo", ex.Message);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<LTXAddVideoICLoRAGuideNode> addGuideNodes = bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);
        Assert.Equal(0.7, addGuideNodes[0].Strength.LiteralAsDouble());
        Assert.Equal(0.3, addGuideNodes[1].Strength.LiteralAsDouble());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        StageRefStore store = new(generator);
        Assert.NotNull(store.Refiner);

        List<LTXVImgToVideoInplaceNode> imgToVideoNodes = bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, imgToVideoNodes.Count);
        LTXVPreprocessNode preprocessNode = Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        foreach (LTXVImgToVideoInplaceNode imgToVideo in imgToVideoNodes)
        {
            AssertGuideReferenceResolvesToPreprocessInput(
                workflow,
                WorkflowBridge.ToPath(imgToVideo.Image.Connection!),
                store.Refiner);
            Assert.Same(preprocessNode.OutputImage, imgToVideo.Image.Connection);
        }

        ImageScaleNode guideScale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 1024 && node.Height.LiteralAsInt() == 1024);
        Assert.Equal("center", guideScale.Crop.LiteralAsString());

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(saveNode.Images.Connection!),
            new JArray(samplers[1].Id, 0)));

        LTXVAudioVAEDecodeNode finalAudioDecode = Assert.IsType<LTXVAudioVAEDecodeNode>(saveNode.Audio.Connection!.Node);
        LTXVSeparateAVLatentNode finalSeparate = Assert.IsType<LTXVSeparateAVLatentNode>(finalAudioDecode.Samples.Connection!.Node);
        JArray finalSeparateAvLatent = WorkflowBridge.ToPath(finalSeparate.AvLatent.Connection!);
        Assert.True(
            OutputTracesBackToSource(workflow, finalSeparateAvLatent, new JArray(samplers[1].Id, 0)),
            $"Expected save audio to decode stage 1 latent [{samplers[1].Id}, 0], but av_latent came from {finalSeparateAvLatent}.");
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode scaleNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == "12" && node.Image.Connection.SlotIndex == 0);
        ImageFromBatchNode imageFromBatch = Assert.Single(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(imageFromBatch.Image.Connection!),
            new JArray(scaleNode.Id, 0)));
        Assert.Equal(768, scaleNode.Width.LiteralAsInt());
        Assert.Equal(448, scaleNode.Height.LiteralAsInt());
        Assert.Equal("lanczos", scaleNode.UpscaleMethod.LiteralAsString());
        Assert.Equal("center", scaleNode.Crop.LiteralAsString());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode scaleNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == "12" && node.Image.Connection.SlotIndex == 0);

        Assert.Equal(1472, scaleNode.Width.LiteralAsInt());
        Assert.Equal(832, scaleNode.Height.LiteralAsInt());
        Assert.Equal("center", scaleNode.Crop.LiteralAsString());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode rootScaleNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == "12" && node.Image.Connection.SlotIndex == 0);
        ImageFromBatchNode imageFromBatch = Assert.Single(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.Equal(960, rootScaleNode.Width.LiteralAsInt());
        Assert.Equal(544, rootScaleNode.Height.LiteralAsInt());
        Assert.Equal("lanczos", rootScaleNode.UpscaleMethod.LiteralAsString());
        Assert.Equal("center", rootScaleNode.Crop.LiteralAsString());
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(imageFromBatch.Image.Connection!),
            new JArray(rootScaleNode.Id, 0)));
    }

    [Fact]
    public void Root_stage_handoff_does_not_leave_orphan_root_resolution_image_scale_after_pre_video_save()
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(1280, generator.CurrentMedia.Width);
        Assert.Equal(1024, generator.CurrentMedia.Height);

        foreach (ImageScaleNode scale in bridge.Graph.NodesOfType<ImageScaleNode>())
        {
            Assert.NotEmpty(bridge.Graph.FindInputsConnectedTo(scale.IMAGE));
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        EmptyLTXVLatentVideoNode emptyLatentNode = Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(768, emptyLatentNode.Width.LiteralAsInt());
        Assert.Equal(448, emptyLatentNode.Height.LiteralAsInt());
    }

    [Fact]
    public void Ltx_empty_latent_audio_matches_video_stages_fps_not_global_swarm_video_fps()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JObject
        {
            ["FPS"] = 30,
            ["Width"] = 512,
            ["Height"] = 512,
            ["Clips"] = new JArray(
                MakeClip(512, 512, MakeStage(models.VideoModel.Name, "Generated", steps: 10)))
        }.ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFPS, 24);
        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        IReadOnlyList<LTXVEmptyLatentAudioNode> emptyAudioNodes = bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>();
        Assert.NotEmpty(emptyAudioNodes);
        Assert.All(
            emptyAudioNodes,
            node => Assert.Equal(30, node.FrameRate.LiteralAsInt()));
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        EmptyLTXVLatentVideoNode emptyLatentNode = Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(721, emptyLatentNode.Length.LiteralAsInt());
        Assert.All(
            bridge.Graph.NodesOfType<ImageFromBatchNode>(),
            node => Assert.Equal(721, node.Length.LiteralAsInt()));
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ComfyNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        LTXVPreprocessNode preprocessNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().OrderBy(node => int.Parse(node.Id)));
        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>().OrderBy(node => int.Parse(node.Id)));

        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);
        AssertSamplerConsumesImgToVideoOutput(workflow, AsWorkflowNode(imgToVideoNode, workflow), AsWorkflowNode(sampler, workflow));
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode rootScaleNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == "12" && node.Image.Connection.SlotIndex == 0);
        LTXVPreprocessNode preprocessNode = Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(preprocessNode.Image.Connection!),
            new JArray(rootScaleNode.Id, 0)));

        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Equal(1.0, imgToVideoNode.Strength.LiteralAsDouble());
        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode rootScaleNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == "12" && node.Image.Connection.SlotIndex == 0);
        LTXVPreprocessNode preprocessNode = Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        StageRefStore store = new(generator);
        Assert.NotNull(store.Base);
        JArray preprocessImageIn = WorkflowBridge.ToPath(preprocessNode.Image.Connection!);
        AssertGuideReferenceResolvesToPreprocessInput(workflow, preprocessImageIn, store.Base);
        Assert.False(OutputTracesBackToSource(workflow, preprocessImageIn, new JArray(rootScaleNode.Id, 0)));

        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Equal(0.55, imgToVideoNode.Strength.LiteralAsDouble());
        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode rootScaleNode = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        LTXVPreprocessNode preprocessNode = Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(preprocessNode.Image.Connection!),
            new JArray(rootScaleNode.Id, 0)));

        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Equal(0.55, imgToVideoNode.Strength.LiteralAsDouble());
        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode rootScaleNode = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        LTXVPreprocessNode preprocessNode = Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Same(rootScaleNode.IMAGE, preprocessNode.Image.Connection);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        LTXVAddGuideNode addGuideNode = Assert.Single(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
        Assert.Equal(2, (int?)addGuideNode.FrameIdx.LiteralAsLong());
        Assert.Equal(0.55, addGuideNode.Strength.LiteralAsDouble());
        Assert.Same(preprocessNode.OutputImage, addGuideNode.Image.Connection);

        LTXVCropGuidesNode cropGuidesNode = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        AssertCropGuidesLatentUsesVideoTensor(workflow, AsWorkflowNode(cropGuidesNode, workflow));
        Assert.Equal(addGuideNode.Id, cropGuidesNode.PositiveInput.Connection!.Node.Id);

        ComfyNode finalDecode = Assert.Single(
            bridge.Graph.Nodes.Values.Where(n => n is VAEDecodeNode or VAEDecodeTiledNode),
            node =>
            {
                INodeInput samples = node.FindInput("samples");
                return samples?.Connection?.Node.Id == cropGuidesNode.Id && samples.Connection.SlotIndex == 2;
            });
        AssertLtxFinalDecodeUsesPlainVaeDecode(AsWorkflowNode(finalDecode, workflow));
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        List<LTXVAddGuideNode> addGuideNodes = bridge.Graph.NodesOfType<LTXVAddGuideNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);
        Assert.Equal(2, (int?)addGuideNodes[0].FrameIdx.LiteralAsLong());
        Assert.Equal(2, (int?)addGuideNodes[1].FrameIdx.LiteralAsLong());
        Assert.Equal(0.55, addGuideNodes[0].Strength.LiteralAsDouble());
        Assert.Equal(0.65, addGuideNodes[1].Strength.LiteralAsDouble());

        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVLatentUpsamplerNode upsamplerNode = Assert.Single(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        Assert.IsType<LTXVCropGuidesNode>(upsamplerNode.Samples.Connection!.Node);
        Assert.Equal(2, upsamplerNode.Samples.Connection.SlotIndex);

        foreach (LTXVCropGuidesNode cropGuidesNode in bridge.Graph.NodesOfType<LTXVCropGuidesNode>())
        {
            AssertCropGuidesLatentUsesVideoTensor(workflow, AsWorkflowNode(cropGuidesNode, workflow));
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>().Count);
        List<(int Width, int Height)> guideScaleSizes = bridge.Graph.NodesOfType<LTXVPreprocessNode>()
            .Select(node => node.Image.Connection?.Node)
            .OfType<ImageScaleNode>()
            .Select(node => (
                Width: node.Width.LiteralAsInt() ?? 0,
                Height: node.Height.LiteralAsInt() ?? 0))
            .OrderBy(size => size.Width)
            .ThenBy(size => size.Height)
            .ToList();

        Assert.Contains((384, 640), guideScaleSizes);
        Assert.Contains((576, 960), guideScaleSizes);
        Assert.Contains((864, 1440), guideScaleSizes);
        foreach (ImageScaleNode scaleNode in bridge.Graph.NodesOfType<ImageScaleNode>())
        {
            int? width = scaleNode.Width.LiteralAsInt();
            int? height = scaleNode.Height.LiteralAsInt();
            if ((width == 576 && height == 960) || (width == 864 && height == 1440))
            {
                Assert.IsNotType<ImageScaleNode>(scaleNode.Image.Connection!.Node);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SetLatentNoiseMaskNode setMaskNode = Assert.Single(
            bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>(),
            node => bridge.Graph.FindInputsConnectedTo(node.LATENT)
                .Any(connection => connection.Input.Name == "audio_latent" && connection.Node is LTXVConcatAVLatentNode));
        LTXVConcatAVLatentNode concatNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>(),
            node => node.AudioLatent.Connection?.Node.Id == setMaskNode.Id && node.AudioLatent.Connection.SlotIndex == 0);
        SolidMaskNode solidMaskNode = Assert.IsType<SolidMaskNode>(setMaskNode.Mask.Connection!.Node);

        Assert.Equal(384, solidMaskNode.Width.LiteralAsInt());
        Assert.Equal(640, solidMaskNode.Height.LiteralAsInt());
        Assert.Same(setMaskNode.LATENT, concatNode.AudioLatent.Connection);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        ImageScaleNode rootScaleNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == "12" && node.Image.Connection.SlotIndex == 0);
        ImageFromBatchNode rootBatchNode = Assert.Single(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowBridge.ToPath(rootBatchNode.Image.Connection!),
            new JArray(rootScaleNode.Id, 0)));
        Wan22ImageToVideoLatentNode wanLatentNode = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>(),
            node => node.StartImage.Connection?.Node.Id == rootBatchNode.Id && node.StartImage.Connection.SlotIndex == 0);
        Assert.Equal(832, wanLatentNode.Width.LiteralAsInt());
        Assert.Equal(480, wanLatentNode.Height.LiteralAsInt());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(SamplerNodesOrdered(bridge));
        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(saveNode.Images.Connection!),
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(SamplerNodesOrdered(bridge));
        Assert.False(workflow.ContainsKey("200"));
        Assert.False(workflow.ContainsKey("201"));
        Assert.False(workflow.ContainsKey("202"));
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(3, SamplerNodesOrdered(bridge).Count);
        Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Equal(3, bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().Count);

        foreach (LTXVConcatAVLatentNode concatNode in bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().Skip(1))
        {
            Assert.IsType<LTXVSeparateAVLatentNode>(concatNode.VideoLatent.Connection!.Node);
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, SamplerNodesOrdered(bridge).Count);
        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(saveNode.Images.Connection!),
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

    [Fact]
    public void Wan_14b_single_ref_does_not_batch_expand_start_image_before_wan_node()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 512,
                height: 512,
                refs: [MakeRef("Refiner", frame: 3, fromEnd: true)],
                MakeStage(models.VideoModel.Name, "Generated", steps: 6)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WanImageToVideoNode wanNode = Assert.Single(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.IsNotType<ImageFromBatchNode>(wanNode.StartImage.Connection!.Node);
    }

    [Fact]
    public void Wan_14b_chained_single_ref_reuses_root_scale_and_previous_sampler_latent()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 384,
                height: 512,
                refs: [MakeRef("Base", frame: 1)],
                MakeStage(models.VideoModel.Name, "Generated", upscale: 1.0, steps: 6),
                MakeStage(models.VideoModel.Name, "Generated", upscale: 1.5, steps: 6)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.Equal(samplers[0].Id, samplers[1].FindInput("latent_image")!.Connection!.Node.Id);
        Assert.Equal(0, samplers[1].FindInput("latent_image")!.Connection!.SlotIndex);

        WanImageToVideoNode wanNode = Assert.Single(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.Same(wanNode.Positive, samplers[1].FindInput("positive")!.Connection);
        Assert.Same(wanNode.Negative, samplers[1].FindInput("negative")!.Connection);

        ImageScaleNode startNode = Assert.IsType<ImageScaleNode>(wanNode.StartImage.Connection!.Node);
        Assert.IsNotType<ImageScaleNode>(startNode.Image.Connection!.Node);
    }

    [Theory]
    [InlineData(0.5, 5)]
    [InlineData(0.7, 3)]
    public void Wan_14b_chained_without_refs_pipes_sampler_directly_and_sets_first_end_step(
        double secondStageControl,
        int expectedFirstEndStep)
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        string stagesJson = new JArray(
            MakeClip(
                width: 384,
                height: 512,
                MakeStage(models.VideoModel.Name, "Generated", upscale: 1.0, steps: 10),
                MakeStage(
                    models.VideoModel.Name,
                    "Generated",
                    control: secondStageControl,
                    upscale: 1.0,
                    steps: 10)))
            .ToString();

        T2IParamInput input = BuildInput(models.BaseModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 121);
        input.Set(T2IParamTypes.VideoFPS, 24);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNoopSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.Equal(samplers[0].Id, samplers[1].FindInput("latent_image")!.Connection!.Node.Id);
        Assert.Equal(0, samplers[1].FindInput("latent_image")!.Connection!.SlotIndex);
        Assert.Equal(expectedFirstEndStep, (int?)samplers[0].FindInput("end_at_step")!.LiteralAsLong());
        Assert.Equal("enable", samplers[0].FindInput("return_with_leftover_noise")!.LiteralAsString());

        INodeOutput sampler0Latent = samplers[0].FindOutput(0);
        Assert.NotNull(sampler0Latent);
        IReadOnlyList<(ComfyNode Node, INodeInput Input)> firstSamplerConsumers = bridge.Graph.FindInputsConnectedTo(sampler0Latent);
        (ComfyNode firstConsumerNode, INodeInput firstConsumerInput) = Assert.Single(firstSamplerConsumers);
        Assert.Equal(samplers[1].Id, firstConsumerNode.Id);
        Assert.Equal("latent_image", firstConsumerInput.Name);
        Assert.Single(bridge.Graph.NodesOfType<WanImageToVideoNode>());
    }

    [Fact]
    public void Wan_14b_chained_image_workflow_pixel_upscale_reuses_previous_sampler_latent()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();
        T2IModelHandler sdHandler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel lowNoiseModel = new(sdHandler, "/tmp", "/tmp/UnitTest_Video_Low.safetensors", "UnitTest_Video_Low.safetensors")
        {
            ModelClass = models.VideoModel.ModelClass
        };
        sdHandler.Models[lowNoiseModel.Name] = lowNoiseModel;

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 384,
                height: 512,
                refs: [MakeRef("Base", frame: 1)],
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, upscale: 1.0, steps: 8),
                MakeStage(lowNoiseModel.Name, "Generated", control: 0.5, upscale: 1.5, steps: 8)))
            .ToString();

        T2IParamInput input = BuildInput(models.BaseModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 121);
        input.Set(T2IParamTypes.VideoFPS, 24);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNoopSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.Equal(samplers[0].Id, samplers[1].FindInput("latent_image")!.Connection!.Node.Id);
        Assert.Equal(0, samplers[1].FindInput("latent_image")!.Connection!.SlotIndex);

        foreach (ImageFromBatchNode fromBatchNode in bridge.Graph.NodesOfType<ImageFromBatchNode>())
        {
            JArray batchImage = WorkflowBridge.ToPath(fromBatchNode.Image.Connection!);
            Assert.False(OutputTracesBackToSource(workflow, batchImage, new JArray(samplers[0].Id, 0)));
        }
    }

    [Fact]
    public void Wan_14b_two_refs_rewrites_to_first_last_frame_node_for_sampler_latent()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 512,
                height: 512,
                refs: [MakeRef("Refiner", frame: 1), MakeRef("Base", frame: 1)],
                MakeStage(models.VideoModel.Name, "Generated", steps: 6)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator2) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WanFirstLastFrameToVideoNode flfNode = Assert.Single(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        ComfyNode samplerNode = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Same(flfNode.Latent, samplerNode.FindInput("latent_image")!.Connection);
        Assert.Equal(1, flfNode.BatchSize.LiteralAsInt());
    }

    [Fact]
    public void Wan_14b_two_matching_refs_reuses_start_scale_for_end_frame()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 384,
                height: 512,
                refs: [MakeRef("Base", frame: 1), MakeRef("Base", frame: 1, fromEnd: true)],
                MakeStage(models.VideoModel.Name, "Generated", steps: 6)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WanFirstLastFrameToVideoNode flfNode = Assert.Single(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Same(flfNode.StartImage.Connection, flfNode.EndImage.Connection);
    }

    [Fact]
    public void Wan_14b_two_different_refs_scales_end_frame_once()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 384,
                height: 512,
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 1, fromEnd: true)],
                MakeStage(models.VideoModel.Name, "Generated", steps: 6)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WanFirstLastFrameToVideoNode flfNode = Assert.Single(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        ImageScaleNode endScaleNode = Assert.IsType<ImageScaleNode>(flfNode.EndImage.Connection!.Node);
        Assert.IsNotType<ImageScaleNode>(endScaleNode.Image.Connection!.Node);
    }

    [Fact]
    public void Wan_14b_chained_two_refs_reuses_first_stage_conditioning()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        string stagesJson = new JArray(
            MakeClipWithRefs(
                width: 384,
                height: 512,
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 1, fromEnd: true)],
                MakeStage(models.VideoModel.Name, "Generated", upscale: 1.0, steps: 6),
                MakeStage(models.VideoModel.Name, "Generated", upscale: 1.5, steps: 6)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WanFirstLastFrameToVideoNode flfNode = Assert.Single(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageFromBatchNode>());

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.Same(flfNode.Positive, samplers[1].FindInput("positive")!.Connection);
        Assert.Same(flfNode.Negative, samplers[1].FindInput("negative")!.Connection);
    }
}
