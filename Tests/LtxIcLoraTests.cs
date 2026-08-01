using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class LtxIcLoraTests
{
    private static WorkflowGenerator.WorkflowGenStep SeedRefinerImageStep() =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            UnknownNode refinerImage = bridge.AddStub("UnitTest_RefinerImage", "12").WithOutputs(WGNodeData.DT_IMAGE);
            g.CurrentMedia = refinerImage.GetOutput(0).ToWGMedia(g, WGNodeData.DT_IMAGE,
                width: 512, height: 512);
        }, 5.0);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static T2IModel RegisterLora(string name)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler loraHandler))
        {
            loraHandler = new T2IModelHandler() { ModelType = "LoRA" };
            Program.T2IModelSets["LoRA"] = loraHandler;
        }
        T2IModel lora = new(loraHandler, "/tmp", $"/tmp/{name}.safetensors", $"{name}.safetensors");
        loraHandler.Models[lora.Name] = lora;
        return lora;
    }

    private static JObject MakeIcLora(
        string lora,
        string source = Constants.IcLoraSourceUpload,
        double strength = 1.0,
        double attentionStrength = 1.0,
        string controlType = Constants.IcLoraControlNone,
        string driveMediaData = null,
        string driveMediaFileName = "drive.mp4",
        IcLoraDriveData? driveData = null)
    {
        JObject entry = new()
        {
            ["lora"] = lora,
            ["driveSource"] = source,
            ["driveData"] = $"{driveData ?? (driveMediaData is null
                ? IcLoraDriveData.None
                : IcLoraDriveData.Visual)}",
            ["strength"] = strength,
            ["attentionStrength"] = attentionStrength,
            ["controlType"] = controlType,
        };
        if (driveMediaData is not null)
        {
            entry["driveMedia"] = new JObject
            {
                ["data"] = driveMediaData,
                ["fileName"] = driveMediaFileName,
            };
        }
        return entry;
    }

    private static (JObject Workflow, WorkflowBridge Bridge) Generate(
        JObject clip,
        TestModelBundle models,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> extraSteps = null)
    {
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps().Concat(extraSteps ?? []));
        return (workflow, WorkflowBridge.Create(workflow));
    }

    [Fact]
    public void Auto_ic_lora_resolves_the_presets_downloaded_weights()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9");

        JObject entry = MakeIcLora(
            IcLoraWeights.AutoModelToken, driveMediaData: "data:video/mp4;base64,QUJD");
        entry["preset"] = "deblur";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXICLoRALoaderModelOnlyNode loader =
            Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal(
            "LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9.safetensors",
            loader.LoraName.LiteralAsString());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
    }

    [Fact]
    public void Auto_ic_lora_without_preset_is_a_user_error()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(MakeIcLora(IcLoraWeights.AutoModelToken));

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps()));
        Assert.Contains("no preset", ex.Message);
    }

    [Fact]
    public void Auto_ic_lora_with_uninstalled_weights_is_a_user_error()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject entry = MakeIcLora(IcLoraWeights.AutoModelToken);
        entry["preset"] = "deblur";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps()));
        Assert.Contains("LTX-2/IC-LoRA/ltx-2_3-22b-ic-lora-deblur-0_9", ex.Message);
    }

    [Fact]
    public void Auto_ic_lora_with_unknown_preset_is_a_user_error()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject entry = MakeIcLora(IcLoraWeights.AutoModelToken);
        entry["preset"] = "unit-test-never-downloaded";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps()));
        Assert.Contains("no known weights", ex.Message);
    }

    [Fact]
    public void Stage_scoped_entry_applies_only_on_its_target_stage()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD");
        entry["stage"] = 1;
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", upscale: 2, steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
    }

    [Fact]
    public void Stage_scope_is_clip_relative_not_global()
    {
        // Stage ids are global across clips; entry.Stage must match the clip's own stage list.
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD");
        entry["stage"] = 0;
        JObject secondClip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        secondClip["icLoras"] = new JArray(entry);
        string stagesJson = new JArray(
            MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10)),
            secondClip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
    }

    [Fact]
    public void Stage_scope_after_skip_marker_does_not_execute()
    {
        // A skipped stage truncates the stage chain, so a later authored target is dormant.
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD");
        entry["stage"] = 1;
        JObject skipped = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        skipped["skipped"] = true;
        JObject clip = MakeClip(skipped, MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Empty(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
    }

    [Fact]
    public void Unscoped_entry_applies_on_every_stage()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", upscale: 2, steps: 10));
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Equal(2, bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count());
    }

    [Fact]
    public void Stage_input_source_drives_guide_from_the_stages_input_frames()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA");
        entry["stage"] = 1;
        entry["driveSource"] = Constants.IcLoraSourceIncoming;
        entry["driveData"] = $"{IcLoraDriveData.Visual}";
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", upscale: 2, steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        LTXAddVideoICLoRAGuideNode guide =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        ImageScaleNode resize =
            Assert.IsType<ImageScaleNode>(NearestGuideImageFramingNode(guide));
        Assert.Equal("center", resize.Crop.LiteralAsString());
        Assert.True(
            GuideImageTracesTo(bridge, guide, resize),
            "Expected the guide image to trace through the stage-dims resize.");
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Stage_input_source_works_on_a_latent_upscale_stage()
    {
        // The stage's input frames are the decoded prior output even when the sampled latent
        // itself is carried forward through LTXVLatentUpsampler (the official Lipdub stage-2 shape).
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA");
        entry["stage"] = 1;
        entry["driveSource"] = Constants.IcLoraSourceIncoming;
        entry["driveData"] = $"{IcLoraDriveData.Visual}";
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated",
                upscale: 2, upscaleMethod: "latent-bislerp", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        // The drive must come from a detached decode of the prior stage's latent, not the live
        // post-video chain (which is re-pointed to this stage's output — a self-referencing loop).
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Incoming_visual_source_uses_the_image_to_video_entry_media()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA");
        entry["driveSource"] = Constants.IcLoraSourceIncoming;
        entry["driveData"] = $"{IcLoraDriveData.Visual}";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        (JObject workflow, _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps());
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Uploaded_drive_media_is_resized_to_stage_dimensions()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXAddVideoICLoRAGuideNode guide =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        ImageScaleNode resize =
            Assert.IsType<ImageScaleNode>(NearestGuideImageFramingNode(guide));
        Assert.Equal("center", resize.Crop.LiteralAsString());
        Assert.True(
            GuideImageTracesTo(bridge, guide, resize),
            "Expected the uploaded drive media to pass through the stage-dims resize.");
    }

    [Fact]
    public void Uploaded_drive_media_uses_the_clips_green_fit_method()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXAddVideoICLoRAGuideNode guide =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        SwarmFrameImageNode frame =
            Assert.IsType<SwarmFrameImageNode>(NearestGuideImageFramingNode(guide));
        Assert.Equal(Constants.ReferenceFramingFitGreen, frame.Method.LiteralAsString());
        Assert.True(
            GuideImageTracesTo(bridge, guide, frame),
            "Expected the IC-LoRA guide to use the clip's green-fit framing node.");
    }

    [Fact]
    public void Two_uploaded_ic_loras_chain_loaders_and_stack_guides()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");
        RegisterLora("UnitTest_IcLoraB");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["controlNetStrength"] = 0.7;
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", strength: 1.2, driveMediaData: "data:video/mp4;base64,QUJD"),
            MakeIcLora("UnitTest_IcLoraB", strength: 0.9, driveMediaData: "data:video/mp4;base64,REVG"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        List<LTXICLoRALoaderModelOnlyNode> loaders = [.. bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>()
            .OrderBy(n => int.Parse(n.Id))];
        Assert.Equal(2, loaders.Count);
        Assert.Equal("UnitTest_IcLoraA.safetensors", loaders[0].LoraName.LiteralAsString());
        Assert.Equal("UnitTest_IcLoraB.safetensors", loaders[1].LoraName.LiteralAsString());
        Assert.Equal(1.2, loaders[0].StrengthModel.LiteralAsDouble() ?? double.NaN, 4);
        Assert.Equal(0.9, loaders[1].StrengthModel.LiteralAsDouble() ?? double.NaN, 4);
        Assert.Same(loaders[0], loaders[1].ModelInput.Connection?.Node);

        List<LTXAddVideoICLoRAGuideNode> guides = [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()
            .OrderBy(n => int.Parse(n.Id))];
        Assert.Equal(2, guides.Count);
        Assert.Same(guides[0], guides[1].PositiveInput.Connection?.Node);
        Assert.Same(guides[0], guides[1].NegativeInput.Connection?.Node);
        Assert.Same(guides[0], guides[1].LatentInput.Connection?.Node);
        Assert.Same(loaders[0], guides[0].LatentDownscaleFactor.Connection?.Node);
        Assert.Same(loaders[1], guides[1].LatentDownscaleFactor.Connection?.Node);
        Assert.Equal(1, guides[0].LatentDownscaleFactor.Connection?.SlotIndex);
        Assert.Equal(0.7, guides[0].Strength.LiteralAsDouble() ?? double.NaN, 4);
        Assert.Equal(0.7, guides[1].Strength.LiteralAsDouble() ?? double.NaN, 4);
    }

    [Fact]
    public void Uploaded_drive_video_loads_b64_with_stripped_prefix_and_feeds_guide()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        SwarmLoadVideoB64Node load = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Equal("QUJD", load.VideoBase64.LiteralAsString());
        GetVideoComponentsNode components = Assert.Single(bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(load, components.Video.Connection?.Node);

        LTXAddVideoICLoRAGuideNode guide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            GuideImageTracesTo(bridge, guide, components),
            "Expected the guide image to trace upstream to the uploaded drive video's components.");
    }

    [Fact]
    public void Attention_strength_below_one_selects_advanced_guide()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");
        RegisterLora("UnitTest_IcLoraB");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD"),
            MakeIcLora("UnitTest_IcLoraB", attentionStrength: 0.65, driveMediaData: "data:video/mp4;base64,REVG"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXAddVideoICLoRAGuideNode basic = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXAddVideoICLoRAGuideAdvancedNode advanced =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());
        Assert.Equal(0.65, advanced.AttentionStrength.LiteralAsDouble() ?? double.NaN, 4);
        Assert.Same(basic, advanced.PositiveInput.Connection?.Node);
    }

    [Fact]
    public void Canny_control_type_splices_canny_between_drive_video_and_guide()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlCanny,
                driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        CannyNode canny = Assert.Single(bridge.Graph.NodesOfType<CannyNode>());
        GetVideoComponentsNode components = Assert.Single(bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(components, canny.Image.Connection?.Node);
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            GuideImageTracesTo(bridge, guide, canny),
            "Expected the guide image to trace upstream through the Canny control signal.");
    }

    [Fact]
    public void Depth_control_type_splices_da3_chain()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlDepth,
                driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LoadDA3ModelNode da3Model = Assert.Single(bridge.Graph.NodesOfType<LoadDA3ModelNode>());
        Assert.Equal(IcLoraWeights.Da3ModelFileName, da3Model.ModelName.LiteralAsString());
        DA3InferenceNode inference = Assert.Single(bridge.Graph.NodesOfType<DA3InferenceNode>());
        Assert.Equal("mono", inference.Mode.LiteralAsString());
        DA3RenderNode render = Assert.Single(bridge.Graph.NodesOfType<DA3RenderNode>());
        Assert.Equal("depth", render.Output.LiteralAsString());
        Assert.Equal("v2_style", $"{render.ExtraInputs?["output.normalization"]}");
        Assert.Same(da3Model, inference.Da3Model.Connection?.Node);
        Assert.Same(inference, render.Da3Geometry.Connection?.Node);
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            GuideImageTracesTo(bridge, guide, render),
            "Expected the guide image to trace upstream through the DA3 depth render.");
    }

    [Fact]
    public void Normal_control_type_splices_moge_chain()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlNormal,
                driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LoadMoGeModelNode mogeModel = Assert.Single(bridge.Graph.NodesOfType<LoadMoGeModelNode>());
        Assert.Equal(IcLoraWeights.MoGeModelFileName, mogeModel.ModelName.LiteralAsString());
        MoGeRenderNode render = Assert.Single(bridge.Graph.NodesOfType<MoGeRenderNode>());
        Assert.Equal("normal_opengl", render.Output.LiteralAsString());
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            GuideImageTracesTo(bridge, guide, render),
            "Expected the guide image to trace upstream through the MoGe normal render.");
    }

    [Fact]
    public void Unknown_control_mode_leaves_drive_images_unmodified()
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Workflow = new JObject(),
        };
        using WorkflowBridge bridge = BridgeSync.For(generator);
        UnknownNode drive = bridge
            .AddStub("UnitTest_UnknownControlDrive", "201")
            .WithOutputs(WGNodeData.DT_IMAGE);
        JArray driveImages = new("201", 0);
        IcLoraPlan plan = new(
            EntryIndex: 0,
            ModelName: "adapter.safetensors",
            UsesAutoModel: false,
            Preset: "custom",
            ModelStrength: 1,
            AttentionStrength: 1,
            IcLoraControlMode.Unknown,
            IcLoraDriveMediaContracts.Resolve(IcLoraDriveData.Visual),
            new(IcLoraDriveMediaKind.Image, null, null),
            new(
                IcLoraMediaSourceKind.Upload,
                Constants.IcLoraSourceUpload,
                IcLoraDriveMediaKind.Image,
                ControlNetIndex: null,
                HasInput: true),
            DimensionDownscaleFactor: 1,
            GuideStrength: 1);

        JArray result = new IcLoraControlSignalBuilder(generator).Apply(
            bridge,
            clipId: 0,
            plan,
            driveImages);

        Assert.True(JToken.DeepEquals(driveImages, result));
        Assert.Empty(bridge.Graph.NodesOfType<LoadMoGeModelNode>());
    }

    [Fact]
    public void Uploaded_image_drive_uses_image_b64_load()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:image/png;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        SwarmLoadImageB64Node load = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("QUJD", load.ImageBase64.LiteralAsString());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            GuideImageTracesTo(bridge, guide, load),
            "Expected the guide image to trace upstream to the uploaded still image.");
    }

    [Fact]
    public void Entry_without_drive_video_is_loader_only()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(MakeIcLora("UnitTest_IcLoraA"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());
    }

    [Fact]
    public void Unresolvable_entry_is_skipped_and_later_entries_still_apply()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraB");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_MissingLora", driveMediaData: "data:video/mp4;base64,QUJD"),
            MakeIcLora("UnitTest_IcLoraB", driveMediaData: "data:video/mp4;base64,REVG"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXICLoRALoaderModelOnlyNode loader =
            Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal("UnitTest_IcLoraB.safetensors", loader.LoraName.LiteralAsString());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
    }

    [Fact]
    public void Uploaded_drive_chain_is_shared_across_stages()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stageA = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject stageB = MakeStage(models.VideoModel.Name, "PreviousStage", steps: 10);
        JObject clip = MakeClip(stageA, stageB);
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count());
    }

    [Fact]
    public void Still_image_drive_is_repeated_to_the_clip_frame_count()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: "data:image/png;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        // The official Ingredients workflow tiles a still reference across the full video length;
        // an ImageFromBatch trim would clamp the 1-frame batch to a single guide frame.
        RepeatImageBatchNode repeat =
            Assert.Single(bridge.Graph.NodesOfType<RepeatImageBatchNode>());
        LTXAddVideoICLoRAGuideNode guide =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(GuideImageTracesTo(bridge, guide, repeat));
    }

    [Fact]
    public void Custom_audio_consuming_ic_lora_feeds_reference_tokens_without_a_visual_guide()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraLipDub");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: "data:video/mp4;base64,QUJD");
        entry["preset"] = "custom-audio";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());

        SwarmLoadVideoB64Node driveLoad =
            Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Equal("QUJD", driveLoad.VideoBase64.LiteralAsString());
        GetVideoComponentsNode driveComponents =
            Assert.Single(bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(driveLoad, driveComponents.Video.Connection?.Node);

        LTXVSetAudioRefTokensNode refTokens =
            Assert.Single(bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        ComfyNode audioStart = refTokens.AudioLatent.Connection?.Node;
        Assert.NotNull(audioStart);
        Assert.True(
            audioStart.Id == driveComponents.Id
            || bridge.Graph.FindNearestUpstream(
                audioStart,
                node => node.Id == driveComponents.Id) is not null,
            "Audio conditioning does not trace to Drive Media's audio stream.");
    }

    [Fact]
    public void Incoming_latent_audio_is_reused_without_decode_encode_or_ensure_churn()
    {
        using SwarmUiTestContext testContext = new();
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = new JObject(),
        };
        WGNodeData latentAudio;
        using (WorkflowBridge bridge = BridgeSync.For(generator))
        {
            UnknownNode source = bridge
                .AddStub("UnitTest_IncomingLatentAudio", "201")
                .WithOutputs(WGNodeData.DT_LATENT_AUDIO);
            latentAudio = source.GetOutput(0).ToWGNodeData(
                generator,
                WGNodeData.DT_LATENT_AUDIO);
        }

        JArray resolved =
            new IcLoraAudioReferenceApplicator(generator)
                .EnsureAudioReferenceLatent(latentAudio);

        using WorkflowBridge graph = WorkflowBridge.Create(generator.Workflow);
        Assert.True(JToken.DeepEquals(new JArray("201", 0), resolved));
        Assert.Empty(graph.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());
        Assert.Empty(graph.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Empty(graph.Graph.NodesOfType<SwarmEnsureAudioNode>());
    }

    [Fact]
    public void Lipdub_drive_audio_feeds_audio_reference_tokens_without_loading_video_or_guide_frames()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraLipDub");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: "data:audio/wav;base64,QUJD",
            driveMediaFileName: "voice.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());

        SwarmLoadAudioB64Node driveAudio =
            Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        LTXVSetAudioRefTokensNode refTokens =
            Assert.Single(bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        ComfyNode audioStart = refTokens.AudioLatent.Connection?.Node;
        Assert.NotNull(audioStart);
        Assert.True(
            audioStart.Id == driveAudio.Id
            || bridge.Graph.FindNearestUpstream(
                audioStart,
                node => node.Id == driveAudio.Id) is not null,
            "LipDub voice conditioning does not trace to the audio Drive Media upload.");
    }

    [Fact]
    public void Lipdub_audio_reference_obeys_the_entry_stage_scope()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraLipDub");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: "data:audio/wav;base64,QUJD",
            driveMediaFileName: "voice.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        entry["stage"] = 1;
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
    }

    [Fact]
    public void Lipdub_all_stages_reuses_one_materialized_audio_sample()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraLipDub");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: "data:audio/wav;base64,QUJD",
            driveMediaFileName: "voice.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Equal(2, bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>().Count());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
    }

    [Fact]
    public void Lipdub_drive_audio_stays_separate_from_the_clip_base_audio_upload()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraLipDub");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: "data:audio/wav;base64,RFJJVkU=",
            driveMediaFileName: "speaker.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["audioSource"] = Constants.AudioSourceUpload;
        clip["uploadedAudio"] = new JObject
        {
            ["data"] = "data:audio/wav;base64,QkFTRQ==",
            ["fileName"] = "base.wav",
        };
        clip["icLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        IReadOnlyList<SwarmLoadAudioB64Node> uploads =
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>();
        Assert.Equal(2, uploads.Count);
        LTXVSetAudioRefTokensNode refTokens =
            Assert.Single(bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        SwarmLoadAudioB64Node referenceUpload =
            bridge.Graph.FindNearestUpstream<SwarmLoadAudioB64Node>(
                refTokens.AudioLatent.Connection!.Node);
        Assert.NotNull(referenceUpload);
        Assert.Contains(uploads, upload => upload.Id != referenceUpload.Id);
        Assert.Empty(bridge.Graph.NodesOfType<AudioConcatNode>());
    }

    [Fact]
    public void Lipdub_and_visual_guide_coexist_across_refinement_without_extra_audio_wrapping()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraLipDub");
        RegisterLora("UnitTest_IcLoraVisual");

        JObject lipDub = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: "data:audio/wav;base64,QUJD",
            driveMediaFileName: "speaker.wav");
        lipDub["preset"] = "lipdub";
        lipDub["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject visual = MakeIcLora(
            "UnitTest_IcLoraVisual",
            driveMediaData: "data:image/png;base64,QUJD",
            driveMediaFileName: "guide.png");
        visual["preset"] = "ingredients";
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(lipDub, visual);

        (JObject workflow, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        AssertAcyclic(bridge);
    }

    [Theory]
    [InlineData(null, "requires uploaded Audio Drive Media")]
    [InlineData(
        "data:image/png;base64,QUJD",
        "cannot consume Audio data from Image media")]
    public void Lipdub_rejects_missing_or_image_drive_media_during_planning(
        string driveMediaData,
        string expectedMessage)
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: driveMediaData);
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["icLoras"] = new JArray(entry);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            new JArray(clip).ToString());
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildCoreVideoWorkflowSteps()));

        Assert.Contains(expectedMessage, ex.Message);
    }

    private static bool GuideImageTracesTo(WorkflowBridge bridge, ComfyNode guide, ComfyNode wanted)
    {
        ComfyNode start = guide.FindInput("image")?.Connection?.Node;
        if (start is null)
        {
            return false;
        }
        if (start.Id == wanted.Id)
        {
            return true;
        }
        return bridge.Graph.FindNearestUpstream(start, node => node.Id == wanted.Id) is not null;
    }

    private static ComfyNode NearestGuideImageFramingNode(ComfyNode guide)
    {
        ComfyNode current = guide.FindInput("image")?.Connection?.Node;
        HashSet<string> visited = [];
        while (current is not null && visited.Add(current.Id))
        {
            if (current is ImageScaleNode or SwarmFrameImageNode)
            {
                return current;
            }
            current =
                current.FindInput("image")?.Connection?.Node
                ?? current.FindInput("images")?.Connection?.Node;
        }
        return null;
    }
}
