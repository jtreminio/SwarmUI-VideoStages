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
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Pins the multi-IC-LoRA behaviors of <c>ControlNetApplicator.ApplyIcLoras</c>: the per-entry loader
/// chain, guide stacking on conditioning + latent, metadata-driven downscale wiring, the Advanced
/// guide for attention strength, uploaded drive videos, control-signal preprocessing, and loader-only
/// entries.
/// </summary>
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
        string videoData = null)
    {
        JObject entry = new()
        {
            ["Lora"] = lora,
            ["Source"] = source,
            ["Strength"] = strength,
            ["AttentionStrength"] = attentionStrength,
            ["ControlType"] = controlType,
        };
        if (videoData is not null)
        {
            entry["Video"] = new JObject { ["Data"] = videoData, ["FileName"] = "drive.mp4" };
        }
        return entry;
    }

    private static (JObject Workflow, WorkflowBridge Bridge) Generate(JObject clip, TestModelBundle models)
    {
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        (JObject workflow, WorkflowGenerator _) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps());
        return (workflow, WorkflowBridge.Create(workflow));
    }

    [Fact]
    public void Auto_ic_lora_resolves_the_presets_downloaded_weights()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9");

        JObject entry = MakeIcLora(
            Constants.IcLoraAutoModel, videoData: "data:video/mp4;base64,QUJD");
        entry["Preset"] = "deblur";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(entry);

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
        clip["IcLoras"] = new JArray(MakeIcLora(Constants.IcLoraAutoModel));

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

        JObject entry = MakeIcLora(Constants.IcLoraAutoModel);
        entry["Preset"] = "deblur";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(entry);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps()));
        Assert.Contains("LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9", ex.Message);
    }

    [Fact]
    public void Auto_ic_lora_with_unknown_preset_is_a_user_error()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject entry = MakeIcLora(Constants.IcLoraAutoModel);
        entry["Preset"] = "unit-test-never-downloaded";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(entry);

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

        JObject entry = MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD");
        entry["Stage"] = 1;
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", upscale: 2, steps: 10));
        clip["IcLoras"] = new JArray(entry);

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

        JObject entry = MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD");
        entry["Stage"] = 0;
        JObject secondClip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        secondClip["IcLoras"] = new JArray(entry);
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
    public void Stage_scope_counts_skipped_stages()
    {
        // entry.Stage indexes the authored stage list, so a skipped earlier stage doesn't shift it.
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD");
        entry["Stage"] = 1;
        JObject skipped = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        skipped["Skipped"] = true;
        JObject clip = MakeClip(skipped, MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
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
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD"));

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
        entry["Stage"] = 1;
        entry["Source"] = Constants.IcLoraSourceStageInput;
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated", upscale: 2, steps: 10));
        clip["IcLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        LTXAddVideoICLoRAGuideNode guide =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        ResizeImageMaskNodeNode resize = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>()
                .Where(n => n.ResizeType.LiteralAsString() == "scale dimensions"));
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
        entry["Stage"] = 1;
        entry["Source"] = Constants.IcLoraSourceStageInput;
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "Generated",
                upscale: 2, upscaleMethod: "latent-bislerp", steps: 10));
        clip["IcLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        // The drive must come from a detached decode of the prior stage's latent, not the live
        // post-video chain (which is re-pointed to this stage's output — a self-referencing loop).
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Stage_input_source_without_refine_placement_is_a_user_error()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA");
        entry["Source"] = Constants.IcLoraSourceStageInput;
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(entry);

        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, new JArray(clip).ToString());
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(input, BuildCoreVideoWorkflowSteps()));
        Assert.Contains("Stage Input", ex.Message);
    }

    [Fact]
    public void Uploaded_drive_media_is_resized_to_stage_dimensions()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXAddVideoICLoRAGuideNode guide =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        ResizeImageMaskNodeNode resize = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>()
                .Where(n => n.ResizeType.LiteralAsString() == "scale dimensions"));
        Assert.True(
            GuideImageTracesTo(bridge, guide, resize),
            "Expected the uploaded drive media to pass through the stage-dims resize.");
    }

    [Fact]
    public void Two_uploaded_ic_loras_chain_loaders_and_stack_guides()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");
        RegisterLora("UnitTest_IcLoraB");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["ControlNetStrength"] = 0.7;
        JObject clip = MakeClip(stage);
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", strength: 1.2, videoData: "data:video/mp4;base64,QUJD"),
            MakeIcLora("UnitTest_IcLoraB", strength: 0.9, videoData: "data:video/mp4;base64,REVG"));

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
        // Guides stack: the second reads the first's conditioning and latent outputs.
        Assert.Same(guides[0], guides[1].PositiveInput.Connection?.Node);
        Assert.Same(guides[0], guides[1].NegativeInput.Connection?.Node);
        Assert.Same(guides[0], guides[1].LatentInput.Connection?.Node);
        // Each guide's downscale factor is wired from ITS loader's metadata output, not a literal.
        Assert.Same(loaders[0], guides[0].LatentDownscaleFactor.Connection?.Node);
        Assert.Same(loaders[1], guides[1].LatentDownscaleFactor.Connection?.Node);
        Assert.Equal(1, guides[0].LatentDownscaleFactor.Connection?.SlotIndex);
        // Stage guide strength applies to every entry's guide.
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
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD"));

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
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD"),
            MakeIcLora("UnitTest_IcLoraB", attentionStrength: 0.65, videoData: "data:video/mp4;base64,REVG"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXAddVideoICLoRAGuideNode basic = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXAddVideoICLoRAGuideAdvancedNode advanced =
            Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());
        Assert.Equal(0.65, advanced.AttentionStrength.LiteralAsDouble() ?? double.NaN, 4);
        // The advanced guide stacks on the basic one.
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
        clip["IcLoras"] = new JArray(
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlCanny,
                videoData: "data:video/mp4;base64,QUJD"));

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
        clip["IcLoras"] = new JArray(
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlDepth,
                videoData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LoadDA3ModelNode da3Model = Assert.Single(bridge.Graph.NodesOfType<LoadDA3ModelNode>());
        Assert.Equal(Constants.Da3ModelFileName, da3Model.ModelName.LiteralAsString());
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
        clip["IcLoras"] = new JArray(
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlNormal,
                videoData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LoadMoGeModelNode mogeModel = Assert.Single(bridge.Graph.NodesOfType<LoadMoGeModelNode>());
        Assert.Equal(Constants.MoGeModelFileName, mogeModel.ModelName.LiteralAsString());
        MoGeRenderNode render = Assert.Single(bridge.Graph.NodesOfType<MoGeRenderNode>());
        Assert.Equal("normal_opengl", render.Output.LiteralAsString());
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            GuideImageTracesTo(bridge, guide, render),
            "Expected the guide image to trace upstream through the MoGe normal render.");
    }

    [Fact]
    public void Uploaded_image_drive_uses_image_b64_load()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        JObject clip = MakeClip(stage);
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", videoData: "data:image/png;base64,QUJD"));

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
        clip["IcLoras"] = new JArray(MakeIcLora("UnitTest_IcLoraA"));

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
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_MissingLora", videoData: "data:video/mp4;base64,QUJD"),
            MakeIcLora("UnitTest_IcLoraB", videoData: "data:video/mp4;base64,REVG"));

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
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD"));

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        // One upload/load chain feeds both stages; loaders and guides stay per-stage.
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
        clip["IcLoras"] = new JArray(
            MakeIcLora("UnitTest_IcLoraA", videoData: "data:image/png;base64,QUJD"));

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
    public void Hdr_entry_splices_the_logc3_postprocess_before_the_save()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraHdr");

        JObject entry = MakeIcLora("UnitTest_IcLoraHdr");
        entry["Preset"] = "hdr";
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        Assert.NotEmpty(bridge.Graph.NodesOfType<LTXVHDRDecodePostprocessNode>());
        foreach (SwarmSaveAnimationWSNode save in bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>())
        {
            Assert.IsType<LTXVHDRDecodePostprocessNode>(save.Images.Connection?.Node);
        }
    }

    [Theory]
    [InlineData("union-control", "some-lora", 2)]
    [InlineData("custom", "ltx-2.3-22b-ic-lora-motion-track-control-ref0.5", 2)]
    [InlineData("pixel-spatial-upscaler-x4", "[AUTO]", 4)]
    [InlineData("pixel-spatial-upscaler-x2", "[AUTO]", 2)]
    [InlineData("deblur", "ltx-2.3-22b-ic-lora-deblur-0.9", 1)]
    public void Known_downscale_factor_derives_from_preset_or_filename(
        string preset,
        string lora,
        int expected)
    {
        ClipSpec clip = new(
            Id: 0,
            Frames: null,
            AudioSource: null,
            IcLoras: [new IcLoraSpec(lora, Constants.IcLoraSourceUpload, 1, 1,
                Constants.IcLoraControlNone, null, Preset: preset)],
            SaveAudioTrack: false,
            ClipLengthFromAudio: false,
            ClipLengthFromControlNet: false,
            ReuseAudio: false,
            UploadedAudio: null,
            ImageRefs: [],
            Stages: []);
        Assert.Equal(expected, ControlNetApplicator.MaxKnownIcLoraDownscaleFactor(clip, 0));
    }

    [Fact]
    public void Drive_audio_voice_ref_wraps_conditioning_after_the_guide()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        RegisterLora("UnitTest_IcLoraA");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", videoData: "data:video/mp4;base64,QUJD");
        entry["DriveAudioRef"] = true;
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["IcLoras"] = new JArray(entry);

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        List<LTXVSetAudioRefTokensNode> refTokens =
            [.. bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>()];
        Assert.NotEmpty(refTokens);
        // The generating stage's wrap sits after the IC-LoRA guide (official LipDub order) and its
        // sample latent traces back to the uploaded drive video's audio split.
        LTXVSetAudioRefTokensNode stageWrap = Assert.Single(
            refTokens,
            node => node.PositiveInput.Connection?.Node is LTXAddVideoICLoRAGuideNode);
        ComfyNode audioStart = stageWrap.AudioLatent.Connection?.Node;
        Assert.NotNull(audioStart);
        Assert.True(
            audioStart is GetVideoComponentsNode
            || bridge.Graph.FindNearestUpstream<GetVideoComponentsNode>(audioStart) is not null);
    }

    [Fact]
    public void Clip_voice_ref_upload_wraps_conditioning_without_ic_loras()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 10));
        clip["AudioSource"] = Constants.AudioSourceVoiceRef;
        clip["UploadedAudio"] = new JObject
        {
            ["Data"] = "data:audio/wav;base64,QUFB",
            ["FileName"] = "voice.wav",
        };

        (JObject _, WorkflowBridge bridge) = Generate(clip, models);
        using WorkflowBridge _ = bridge;

        LTXVSetAudioRefTokensNode refTokens =
            Assert.Single(bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.NotNull(refTokens.PositiveInput.Connection);
        Assert.NotNull(refTokens.AudioLatent.Connection);
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
}
