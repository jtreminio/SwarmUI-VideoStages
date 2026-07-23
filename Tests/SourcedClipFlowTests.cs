using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    // Duration 0.6s at the harness's 24 fps aligns to 17 spec frames (8n+1); the default continue
    // overlap (8) then resolves to a 9-frame window. StartSeconds 1.0 slices from frame 24.
    private const int SourcedClipFrames = 17;
    private const double SourcedClipDuration = 0.6;
    private const double SourcedStartSeconds = 1.0;

    private static readonly string[] SourcedClipFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    private static JObject MakeSourcedClip(TestModelBundle models)
    {
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        ((JObject)((JArray)clip["Stages"])[0]).Remove("ImageReference");
        clip["Duration"] = SourcedClipDuration;
        clip["SourceVideo"] = new JObject
        {
            ["Data"] = "data:video/mp4;base64," + Convert.ToBase64String([0x11, 0x22, 0x33]),
            ["FileName"] = "footage.mp4",
            ["StartSeconds"] = SourcedStartSeconds
        };
        return clip;
    }

    private static JObject MakeGeneratedClip(TestModelBundle models)
    {
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        clip["Duration"] = SourcedClipDuration;
        return clip;
    }

    private static (JObject Workflow, WorkflowGenerator Generator) GenerateSourcedFlow(
        TestModelBundle models, params JObject[] clips)
    {
        T2IParamInput input = BuildNativeInput(
            models.BaseModel, models.VideoModel, MakeRootConfig(512, 512, clips).ToString());
        return WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
    }

    private static SwarmFrameWindowNode AssertSourcedConformChain(WorkflowBridge bridge)
    {
        SwarmLoadVideoB64Node loadVideo = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Equal(loadVideo.Id, components.Video.Connection!.Node.Id);

        SwarmVideoResampleFPSNode resample = Assert.Single(
            bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.Equal(24.0, resample.FpsOut.LiteralAsDouble());
        Assert.Equal(components.Id, resample.FpsIn.Connection!.Node.Id);
        Assert.Equal(2, resample.FpsIn.Connection!.SlotIndex);
        Assert.Equal(components.Id, resample.ImagesInput.Connection!.Node.Id);
        Assert.Equal(0, resample.ImagesInput.Connection!.SlotIndex);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Equal((int)Math.Round(SourcedStartSeconds * 24), window.StartFrame.LiteralAsInt());
        Assert.Equal(SourcedClipFrames, window.FrameCount.LiteralAsInt());
        Assert.Equal(resample.Id, window.ImagesInput.Connection!.Node.Id);

        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            n => n.Image.Connection?.Node.Id == window.Id);
        Assert.Equal(512, scale.Width.LiteralAsInt());
        Assert.Equal(512, scale.Height.LiteralAsInt());

        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            n => ReachesUpstream(bridge, n, components.Id));
        Assert.Equal(SourcedStartSeconds, trim.StartIndex.LiteralAsDouble());
        Assert.Equal(SourcedClipFrames / 24.0, trim.Duration.LiteralAsDouble()!.Value, precision: 6);

        return window;
    }

    [Fact]
    public void Sourced_clip_replaces_generation_with_conform_chain_and_merges()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        (JObject workflow, WorkflowGenerator g) = GenerateSourcedFlow(
            models, MakeGeneratedClip(models), MakeSourcedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);

        // Per-clip refine: the sourced clip's stage 0 (Control 0.5) refines its own footage — its
        // sampler (start_at_step floor(10 * 0.5) = 5) is seeded from the conform chain — and its
        // output joins the cross-clip merge.
        SwarmKSamplerNode sourcedSampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            sampler => ReachesUpstream(bridge, sampler, window.Id));
        Assert.Equal(5, sourcedSampler.StartAtStep.LiteralAsInt());
        Assert.Contains(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>(),
            batch => ReachesUpstream(bridge, batch, window.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_LipDub_keeps_init_video_visuals_and_uses_drive_video_audio_only()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel lipDub = new(
            loraHandler,
            "/tmp",
            "/tmp/UnitTest_LipDub.safetensors",
            "UnitTest_LipDub.safetensors");
        loraHandler.Models[lipDub.Name] = lipDub;

        JObject sourced = MakeSourcedClip(models);
        sourced["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = lipDub.Name,
            ["Preset"] = IcLoraDriveMediaContracts.LipDubPreset,
            ["Source"] = Constants.IcLoraSourceUpload,
            ["DriveMedia"] = new JObject
            {
                ["Data"] = "data:video/mp4;base64,RFJJVkU=",
                ["FileName"] = "target-voice.mp4",
            },
        });

        (JObject workflow, WorkflowGenerator _generator) =
            GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node sourceLoad = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>(),
            node => node.VideoBase64.LiteralAsString() != "RFJJVkU=");
        SwarmLoadVideoB64Node driveLoad = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>(),
            node => node.VideoBase64.LiteralAsString() == "RFJJVkU=");
        GetVideoComponentsNode driveComponents = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>(),
            node => node.Video.Connection?.Node.Id == driveLoad.Id);
        SwarmFrameWindowNode sourceWindow = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>(),
            node => ReachesUpstream(bridge, node, sourceLoad.Id));

        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVSetAudioRefTokensNode refTokens =
            Assert.Single(bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.True(
            ReachesUpstream(bridge, refTokens.AudioLatent.Connection!.Node, driveComponents.Id),
            "LipDub reference tokens do not trace to the Drive Media video's audio.");
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.True(
            ReachesUpstream(bridge, sampler.LatentImage.Connection!.Node, sourceWindow.Id),
            "The sampler's visuals do not trace to the init video.");
        Assert.False(
            ReachesUpstream(bridge, sampler.LatentImage.Connection!.Node, driveComponents.Id),
            "Drive Media video frames leaked into the generated visual path.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_second_stage_refines_the_passthrough_footage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        ((JArray)sourced["Stages"]).Add(
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 12));
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // Stage 0 (Control 0.5) now refines the footage directly, and the refine stage chains off
        // it: two samplers — stage 0 at start_at_step floor(10 * 0.5) = 5 seeded from the window,
        // the refine at floor(12 * 0.5) = 6 chained onto stage 0's output.
        List<SwarmKSamplerNode> samplers = [.. SamplerNodesOrdered(bridge)];
        Assert.Equal(2, samplers.Count);
        SwarmKSamplerNode stage0 = Assert.Single(
            samplers, s => s.StartAtStep.LiteralAsInt() == 5);
        SwarmKSamplerNode refine = Assert.Single(
            samplers, s => s.StartAtStep.LiteralAsInt() == 6);
        Assert.True(
            ReachesUpstream(bridge, stage0.LatentImage.Connection!.Node, window.Id),
            "Stage 0 does not sample the conformed footage directly.");
        Assert.True(
            ReachesUpstream(bridge, refine.LatentImage.Connection!.Node, stage0.Id),
            "Refine stage does not chain onto stage 0's output.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_loader_only_ic_lora_emits_a_guide_on_the_refine_stage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel icLora = new(
            loraHandler, "/tmp", "/tmp/UnitTest_IcLoraUpscaler.safetensors", "UnitTest_IcLoraUpscaler.safetensors");
        loraHandler.Models[icLora.Name] = icLora;

        // Passthrough stage 0 (Control 0) + a ×2 refine stage. An Upload/no-media IC-LoRA on a
        // sourced clip now drives implicitly from the stage's incoming frames, so it is no longer
        // loader-only: it emits the full guide on the sampling refine stage.
        JObject sourced = MakeSourcedClip(models);
        ((JArray)sourced["Stages"])[0]["Control"] = 0.0;
        ((JArray)sourced["Stages"]).Add(
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, upscale: 2.0, steps: 12));
        sourced["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = "UnitTest_IcLoraUpscaler",
            ["Source"] = Constants.IcLoraSourceUpload,
        });
        // Paired with a generated lead clip: the cross-clip merge owns the final save, so the run
        // does not take the lone-sourced-clip root-save retarget path.
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, MakeGeneratedClip(models), sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Exactly one loader (the passthrough stage 0 has no sampler, so no loader dangles there),
        // one guide, and one crop-guides node — all on the refine stage's sampler chain.
        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        SwarmKSamplerNode sampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            s => ReachesUpstream(bridge, s.Model.Connection!.Node, loader.Id));
        Assert.True(
            ReachesUpstream(bridge, sampler, guide.Id),
            "The refine sampler does not consume the IC-LoRA guide's latent.");
        Assert.True(
            ReachesUpstream(bridge, crop, sampler.Id),
            "LTXVCropGuides does not sit after the refine sampler.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_implicit_ic_lora_drive_guides_from_the_footage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel icLora = new(
            loraHandler, "/tmp", "/tmp/UnitTest_IcLoraDrive.safetensors", "UnitTest_IcLoraDrive.safetensors");
        loraHandler.Models[icLora.Name] = icLora;

        // Single sampling stage (Control 0.5, steps 10) + an Upload/no-media IC-LoRA: the sourced
        // footage is the implicit drive, so stage 0 emits the full guide from its incoming frames.
        // Paired with a generated lead clip so the merge owns the save (the lone-sourced retarget
        // path is covered by Lone_sourced_clip_with_ic_lora_guide_saves_decoded_audio_not_a_latent).
        JObject sourced = MakeSourcedClip(models);
        sourced["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = "UnitTest_IcLoraDrive",
            ["Source"] = Constants.IcLoraSourceUpload,
        });
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, MakeGeneratedClip(models), sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // No footage self-reinjection: sourced stage 0 is encode-only.
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        // The implicit-drive guide encodes from the stage's incoming footage and feeds the video
        // latent into the AV concat that the sampler consumes.
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            ReachesUpstream(bridge, guide.Image.Connection!.Node, window.Id),
            "The guide image does not trace back to the sourced footage conform chain.");
        LTXVConcatAVLatentNode concat = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>(),
            n => n.VideoLatent.Connection?.Node.Id == guide.Id);
        Assert.NotNull(concat);
        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        SwarmKSamplerNode sampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            s => ReachesUpstream(bridge, s, window.Id));
        Assert.Equal(5, sampler.StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, crop, sampler.Id),
            "LTXVCropGuides does not sit after the sourced clip's sampler.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_stage_input_source_is_legal_at_stage_zero_and_emits_a_guide()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel icLora = new(
            loraHandler, "/tmp", "/tmp/UnitTest_IcLoraDrive.safetensors", "UnitTest_IcLoraDrive.safetensors");
        loraHandler.Models[icLora.Name] = icLora;

        // On a sourced clip, stage 0's incoming frames ARE the footage, so an explicit "Stage Input"
        // drive at Stage 0 is legal (it throws on an unsourced clip — see LtxIcLoraTests
        // Stage_input_source_without_refine_placement_is_a_user_error) and emits a guide.
        JObject sourced = MakeSourcedClip(models);
        sourced["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = "UnitTest_IcLoraDrive",
            ["Source"] = Constants.IcLoraSourceStageInput,
            ["Stage"] = 0,
        });
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, MakeGeneratedClip(models), sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.True(
            ReachesUpstream(bridge, guide.Image.Connection!.Node, window.Id),
            "The Stage-Input guide image does not trace back to the sourced footage.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Lone_sourced_clip_with_ic_lora_guide_saves_decoded_audio_not_a_latent()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel icLora = new(
            loraHandler, "/tmp", "/tmp/UnitTest_IcLoraDrive.safetensors", "UnitTest_IcLoraDrive.safetensors");
        loraHandler.Models[icLora.Name] = icLora;

        // A LONE sourced clip carrying an IC-LoRA guide takes the root-save retarget path. The guide
        // extends the video latent through LTXVCropGuides, so the concat AV latent no longer decodes
        // into a clean separate node; the fixed AttachDecodedLtxAudioFromCurrentVideo adds an
        // explicit LTXVAudioVAEDecode so the save's audio is decoded AUDIO, not a raw latent.
        JObject sourced = MakeSourcedClip(models);
        sourced["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = "UnitTest_IcLoraDrive",
            ["Source"] = Constants.IcLoraSourceUpload,
        });
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // The implicit-drive guide + crop guides are present; no footage self-reinjection.
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        // The save's audio is fed by a decoded-audio node, not a raw latent route.
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "Saved video does not trace back to the sourced footage chain.");
        Assert.IsType<LTXVAudioVAEDecodeNode>(save.Audio.Connection!.Node);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_pixel_upscale_retargets_the_conform_scale_instead_of_chaining()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        // Passthrough stage 0 (Control 0): the ×2 pixel refine is the only sampling stage.
        ((JArray)sourced["Stages"])[0]["Control"] = 0.0;
        ((JArray)sourced["Stages"]).Add(
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, upscale: 2.0, steps: 12));
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // The refine stage's ×2 pixel upscale re-fits the conform scale in place: one resample from
        // the raw footage straight to the final dims, not a chained base-dims intermediate.
        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            n => ReachesUpstream(bridge, n, window.Id));
        Assert.Equal(window.Id, scale.Image.Connection!.Node.Id);
        Assert.Equal(1024, scale.Width.LiteralAsInt());
        Assert.Equal(1024, scale.Height.LiteralAsInt());
        Assert.Equal("center", scale.Crop.LiteralAsString());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Later_stage_referencing_the_passthrough_does_not_refit_the_refine_stages_input_scale()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        // Passthrough stage 0 (Control 0): stage 1 is the refine stage that owns the conform scale.
        ((JArray)sourced["Stages"])[0]["Control"] = 0.0;
        ((JArray)sourced["Stages"]).Add(
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, upscale: 2.0, steps: 12));
        ((JArray)sourced["Stages"]).Add(
            MakeStage(models.VideoModel.Name, "Stage0", control: 0.5, upscale: 2.0, steps: 12));
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Stage 1 retargeted the conform scale to its own input dims (512×2). Stage 2's Stage0
        // guide runs at stage 2's dims (×2 again) — it must not re-fit that now-load-bearing node
        // out from under stage 1's encode.
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        ImageScaleNode conform = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            n => n.Image.Connection?.Node.Id == window.Id);
        Assert.Equal(1024, conform.Width.LiteralAsInt());
        Assert.Equal(1024, conform.Height.LiteralAsInt());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_retake_masks_regenerate_a_window_of_the_footage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        sourced["Retake"] = new JObject
        {
            ["StartSeconds"] = 0.2,
            ["LengthSeconds"] = 0.2,
            ["Strength"] = 1.0
        };
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        Assert.True(
            ReachesUpstream(bridge, maskNode, window.Id),
            "Retake noise mask does not apply to the sourced footage's latent.");
        SwarmKSamplerNode sampler = Assert.Single(
            SamplerNodesOrdered(bridge),
            s => ReachesUpstream(bridge, s, window.Id));
        Assert.Equal(0, sampler.StartAtStep.LiteralAsInt());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Continue_from_sourced_clip_freezes_its_tail_as_next_clip_context()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        sourced["BoundaryOut"] = "continue";
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, sourced, MakeGeneratedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        int windowFrames = Ltx2BoundaryPolicy.DefaultFrames + 1;
        List<ImageFromBatchNode> tailSlices = [.. bridge.Graph.NodesOfType<ImageFromBatchNode>()
            .Where(n => n.BatchIndex.LiteralAsInt() == SourcedClipFrames - windowFrames
                && n.Length.LiteralAsInt() == windowFrames
                && ReachesUpstream(bridge, n, window.Id))];
        Assert.NotEmpty(tailSlices);

        Assert.Contains(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>(),
            n => n.Strength.LiteralAsDouble() == 1.0
                && tailSlices.Any(slice => ReachesUpstream(bridge, n, slice.Id)));

        SwarmRampMaskBatchNode ramp = Assert.Single(
            bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(windowFrames, ramp.Frames.LiteralAsInt());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("cut")]
    [InlineData("crossfade")]
    public void Sourced_lead_clip_boundary_retargets_the_root_save_to_the_merge(string boundaryOut)
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Sourced LEAD clip + a generated clip: the root generation survives as the generated
        // clip's source donor and gets pixel-conformed to the timeline resolution. The core save
        // consumes the PRE-conform root output, so the merge retarget must still catch it — a
        // missed retarget ships the unrelated root generation as a second output video.
        JObject sourced = MakeSourcedClip(models);
        sourced["BoundaryOut"] = boundaryOut;
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, sourced, MakeGeneratedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        List<SwarmSaveAnimationWSNode> saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        string diag = string.Join("; ", saves.Select(s =>
            $"save {s.Id} <- {s.Images.Connection?.Node.Id}"
            + $" reachesWindow={ReachesUpstream(bridge, s.Images.Connection!.Node, window.Id)}"));
        Assert.True(saves.Count == 1, $"Expected one save, got {saves.Count}: {diag}");
        Assert.True(
            ReachesUpstream(bridge, saves[0].Images.Connection!.Node, window.Id),
            "The save does not trace to the cross-clip merge (it still points at the root "
            + $"generation output). {diag}");
        // The join itself engaged: continue/crossfade blend at the seam, a cut must not.
        Assert.Equal(
            boundaryOut != Constants.BoundaryOutCut,
            bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>().Any());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_lead_then_generated_clip_in_real_native_i2v_step_order_publishes_only_the_timeline()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Exercise the actual host priority-10 save preparation and priority-11 native I2V step,
        // rather than the already-decoded priority-11 fixture used by most sourced-clip tests.
        // Clip 0 owns uploaded footage while clip 1 owns the generated root handoff.
        JObject sourced = MakeSourcedClip(models);
        sourced["BoundaryOut"] = Constants.BoundaryOutCut;
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(512, 512, sourced, MakeGeneratedClip(models)).ToString());

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowStepsWithPreVideoSave(),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        List<SwarmKSamplerNode> samplers = [.. SamplerNodesOrdered(bridge)];
        Assert.Equal(2, samplers.Count);
        Assert.Single(
            samplers,
            sampler => ReachesUpstream(bridge, sampler, window.Id));

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "The sole published video does not trace to the mixed sourced/generated timeline.");
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(save.Images.Connection),
            generator.CurrentMedia.Path));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Passthrough_sourced_lead_continue_join_samples_only_the_generated_clip()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Control 0 on the sourced clip's only stage: upload a video and join it to a generated
        // clip without altering the footage — no sampler for clip 0, just the conform chain
        // feeding the merge.
        JObject sourced = MakeSourcedClip(models);
        ((JArray)sourced["Stages"])[0]["Control"] = 0.0;
        sourced["BoundaryOut"] = "continue";
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, sourced, MakeGeneratedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // One sampler total — the generated clip's. (It still traces to the footage: the
        // continue boundary freezes clip 0's tail as its opening latent context.)
        Assert.Single(SamplerNodesOrdered(bridge));
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "The save does not trace to the merged output containing the footage.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Continue_from_sourced_clip_keeps_spec_dims_for_the_generated_clip()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Spec dims deliberately differ from the harness's core-param (root media) dims: the
        // surviving root media must be conformed to the SPEC resolution so the generated clip runs
        // at the same dims as the sourced clip. Left at core dims, the merge degrades to a hard cut
        // that repeats the continuity overlap frames.
        JObject sourced = MakeSourcedClip(models);
        sourced["BoundaryOut"] = "continue";
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(768, 1024, sourced, MakeGeneratedClip(models)).ToString());
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The overlap plan survived: seam blend + ramp instead of a plain full concat.
        SwarmRampMaskBatchNode ramp = Assert.Single(
            bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(Ltx2BoundaryPolicy.DefaultFrames + 1, ramp.Frames.LiteralAsInt());
        Assert.NotEmpty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Continue_into_sourced_clip_degrades_to_cut()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject generated = MakeGeneratedClip(models);
        generated["BoundaryOut"] = "continue";
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(
            models, generated, MakeSourcedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Fixed footage can't be conditioned on the previous clip's tail: the boundary degrades to
        // a plain cut concat, so no seam-collapse blend or ramp mask exists. (ImgToVideoInplace
        // nodes still appear from ordinary stage conditioning, so their absence is not the signal.)
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLaplacianPyramidBlendNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Contains(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>(),
            batch => ReachesUpstream(bridge, batch, window.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Single_sourced_clip_outputs_the_conformed_footage_with_its_audio()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Passthrough stage 0 (Control 0): the conformed footage IS the output, no sampling at all.
        JObject sourced = MakeSourcedClip(models);
        ((JArray)sourced["Stages"])[0]["Control"] = 0.0;
        (JObject workflow, WorkflowGenerator g) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        Assert.Equal(SourcedClipFrames, g.CurrentMedia.Frames);
        INodeOutput currentOutput = bridge.ResolvePath((JArray)g.CurrentMedia.Path);
        Assert.True(
            ReachesUpstream(bridge, currentOutput.Node, window.Id),
            "Final media does not trace back to the sourced footage chain.");
        // Passthrough stage + pruned root generation: the workflow samples nothing at all.
        Assert.Empty(SamplerNodesOrdered(bridge));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip0_in_text_to_video_flow_encodes_the_footage_not_an_empty_latent()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel, MakeRootConfig(512, 512, MakeSourcedClip(models)).ToString());
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildTextToVideoSteps(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The text-to-video root replacement must not hijack a sourced clip's first stage: that
        // path built an EmptyLTXVLatentVideo and orphaned the whole conform chain, so the clip
        // rendered noise instead of the uploaded footage.
        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        // Stage 0 (Control 0.5) refines the footage: exactly one sampler, seeded from the conform
        // chain (start_at_step floor(10 * 0.5) = 5), and the empty-latent root path is pruned.
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Equal(5, sampler.StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, sampler, window.Id),
            "The refine sampler does not trace back to the sourced footage chain.");

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "Saved video does not trace back to the sourced footage chain.");
        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Audio.Connection!.Node, trim.Id),
            "Saved audio does not trace back to the source video's trimmed track.");
        AssertWorkflowHasNoCycles(workflow);
    }

    // Mirrors the REAL host graph at VideoStages time for a text-to-video root: the raw AV latent
    // is still undecoded — the real core priority-10 step (CorePreVideoSavePrepStep) then authors
    // the separate/decode/save itself, leaving CurrentMedia.AttachedAudio as a LATENT audio ref.
    // The harness seeds that author decode+save wholesale never reproduce that state.
    private static WorkflowGenerator.WorkflowGenStep SeedRawTextToVideoAvLatentRootStep() =>
        new(g =>
        {
            T2IModel model = g.UserInput.Get(T2IParamTypes.Model, null);
            g.FinalLoadedModel = model;
            g.FinalLoadedModelList = model is null ? [] : [model];

            using var bridge = BridgeSync.For(g);
            UnknownNode unet = bridge.AddStub("UnitTest_RootUnet", "4").WithOutputs(WGNodeData.DT_MODEL, "CLIP");
            g.CurrentModel = unet.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = unet.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);
            UnknownNode vaeLoader = bridge.AddStub("UnitTest_RootVae", "101").WithOutputs(WGNodeData.DT_VAE);
            g.CurrentVae = vaeLoader.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);
            UnknownNode audioVaeLoader = bridge.AddStub("UnitTest_RootAudioVae", "102").WithOutputs(WGNodeData.DT_VAE);
            g.CurrentAudioVae = audioVaeLoader.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIOVAE);

            UnknownNode emptyVideo = bridge.AddStub("UnitTest_EmptyVideoLatent", "5").WithOutputs("LATENT");
            UnknownNode emptyAudio = bridge.AddStub("UnitTest_EmptyAudioLatent", "103").WithOutputs("LATENT");
            LTXVConcatAVLatentNode concat = new();
            concat.VideoLatent.ConnectToUntyped(emptyVideo.GetOutput(0));
            concat.AudioLatent.ConnectToUntyped(emptyAudio.GetOutput(0));
            bridge.AddNode(concat, "104");
            SwarmKSamplerNode sampler = bridge.AddNode(new SwarmKSamplerNode(), "10");
            sampler.Model.ConnectToUntyped(unet.GetOutput(0));
            sampler.LatentImage.ConnectTo(concat.Latent);

            // A dead consumer pinning the root latent — the live flows grow these transiently
            // (the root's audio-decode sibling, detached guide decodes). An upstream-only prune
            // stops at the sampler because of it; the dead-component sweep must remove both.
            LTXVSeparateAVLatentNode strayDetach = new();
            strayDetach.AvLatent.ConnectTo(sampler.LATENT);
            bridge.AddNode(strayDetach, "11");

            g.CurrentMedia = new WGNodeData(
                new JArray("10", 0), g, WGNodeData.DT_LATENT_AUDIOVIDEO, T2IModelClassSorter.CompatLtxv2);
        }, 4);

    [Fact]
    public void Sourced_clip_in_real_text_to_video_step_order_leaves_no_dangling_root_sampler()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel stageLora = new(
            loraHandler, "/tmp", "/tmp/UnitTest_StageLora.safetensors", "UnitTest_StageLora.safetensors");
        loraHandler.Models[stageLora.Name] = stageLora;

        JObject sourced = MakeSourcedClip(models);
        // Payload parity with the real dangling-sampler report: continue boundary, per-stage loras,
        // an implicit-drive IC-LoRA (Upload/no-media on a sourced clip), and a ×2 pixel refine stage.
        sourced["BoundaryOut"] = "continue";
        sourced["BoundaryOutOverlap"] = 40;
        JArray stageLoras = new(new JObject { ["Name"] = "UnitTest_StageLora", ["Weight"] = 1.0 });
        ((JArray)sourced["Stages"])[0]["Loras"] = stageLoras;
        JObject refineStage = MakeStage(
            models.VideoModel.Name, "PreviousStage", control: 0.5, upscale: 2.0, steps: 12);
        refineStage["Loras"] = stageLoras.DeepClone();
        ((JArray)sourced["Stages"]).Add(refineStage);
        sourced["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = "UnitTest_StageLora",
            ["Source"] = Constants.IcLoraSourceUpload,
        });
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel, MakeRootConfig(512, 512, sourced).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 0);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 0);
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            new[] { SeedRawTextToVideoAvLatentRootStep(), WorkflowTestHarness.CorePreVideoSavePrepStep() }
                .Concat(WorkflowTestHarness.VideoStagesSteps()),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The root t2v generation must be fully pruned — only the clip's own stages sample. A
        // surviving root sampler is a dangling chain the host post-cleanup cannot remove
        // (SwarmKSampler is not in its unused-classes allowlist). Stage 0 (Control 0.5) refines the
        // footage at start_at_step 5, the ×2 refine at floor(12 * 0.5) = 6; both trace to the window.
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        List<SwarmKSamplerNode> samplers = [.. SamplerNodesOrdered(bridge)];
        Assert.Equal(2, samplers.Count);
        Assert.All(
            samplers,
            s => Assert.True(
                ReachesUpstream(bridge, s, window.Id),
                "A surviving sampler does not trace back to the clip's footage window."));
        Assert.Single(samplers, s => s.StartAtStep.LiteralAsInt() == 5);
        Assert.Single(samplers, s => s.StartAtStep.LiteralAsInt() == 6);
        Assert.False(workflow.ContainsKey("10"), "Root t2v sampler was left dangling.");
        Assert.False(workflow.ContainsKey("11"), "Dead root latent consumer was left dangling.");
        Assert.False(workflow.ContainsKey("104"), "Root AV concat was left dangling.");

        // The all-stages implicit-drive IC-LoRA emits a loader + guide + crop on each of the two
        // sampling stages, and the save's audio is decoded (not a raw latent) after the root-save
        // retarget.
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count());
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.IsType<LTXVAudioVAEDecodeNode>(save.Audio.Connection!.Node);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Generated_clip_replacing_raw_t2v_av_root_does_not_pin_the_root_sampler()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // This is the raw host T2V state before its priority-10 separate/decode/save pass. Unlike
        // the sourced tests above, no uploaded footage participates: the generated VideoStages
        // clip alone replaces the root AV generation and must own the only surviving output.
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeRootConfig(512, 512, MakeGeneratedClip(models)).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 0);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 0);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            new[] { SeedRawTextToVideoAvLatentRootStep(), WorkflowTestHarness.CorePreVideoSavePrepStep() }
                .Concat(WorkflowTestHarness.VideoStagesSteps()),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.False(workflow.ContainsKey("10"), "The discarded root T2V sampler remained pinned.");
        Assert.False(workflow.ContainsKey("11"), "The root sampler's dead detach consumer remained.");
        Assert.False(workflow.ContainsKey("104"), "The discarded root AV concat remained.");
        Assert.Single(SamplerNodesOrdered(bridge));

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(save.Images.Connection!),
            generator.CurrentMedia.Path));
        Assert.IsType<LTXVAudioVAEDecodeNode>(save.Audio.Connection!.Node);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Do_not_save_generated_t2v_timeline_still_cleans_the_discarded_root()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeRootConfig(512, 512, MakeGeneratedClip(models)).ToString());
        input.Set(T2IParamTypes.DoNotSave, true);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeTextToVideoStepsWithPreCoreVideo(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.False(workflow.ContainsKey("200"));
        Assert.False(workflow.ContainsKey("201"));
        Assert.False(workflow.ContainsKey("202"));
        Assert.Empty(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.NotNull(bridge.ResolvePath((JArray)generator.CurrentMedia.Path));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_lead_with_generated_clip_in_t2v_drops_the_root_generation()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Sourced LEAD clip + generated clip in a real-order text-to-video run: the generated
        // clip replaces the root with its own empty latent and self-generates audio, so the root
        // generation has NO consumer and must be dropped. Regression: the root's audio latent was
        // wired in as the generated clip's audio init, pinning the whole unrelated root sampler
        // (a third SwarmKSampler) alive in the graph.
        JObject sourced = MakeSourcedClip(models);
        sourced["BoundaryOut"] = "continue";
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeRootConfig(512, 512, sourced, MakeGeneratedClip(models)).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 0);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 0);
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            new[] { SeedRawTextToVideoAvLatentRootStep(), WorkflowTestHarness.CorePreVideoSavePrepStep() }
                .Concat(WorkflowTestHarness.VideoStagesSteps()),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // Exactly two samplers — sourced clip 0's refine and the generated clip. The root t2v
        // sampler ("10") and its AV chain must be gone.
        List<SwarmKSamplerNode> samplers = [.. SamplerNodesOrdered(bridge)];
        Assert.True(
            samplers.Count == 2,
            $"Expected 2 samplers, got {samplers.Count}: "
            + string.Join(", ", samplers.Select(s => s.Id)));
        Assert.False(workflow.ContainsKey("10"), "Root t2v sampler was left in the workflow.");
        Assert.False(workflow.ContainsKey("104"), "Root AV concat was left dangling.");
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "The save does not trace to the cross-clip merge.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip0_in_text_to_video_flow_takes_over_the_root_save_and_drops_the_root_generation()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel, MakeRootConfig(512, 512, MakeSourcedClip(models)).ToString());
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeTextToVideoStepsWithPreCoreVideo(attachAudioToCurrentMedia: true),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The native t2v root chain (sampler 200 / separate 201 / decode 202) must not survive as a
        // dangling generation: its save is retargeted onto the sourced clip's output and the rest
        // pruned.
        Assert.False(workflow.ContainsKey("200"));
        Assert.False(workflow.ContainsKey("201"));
        Assert.False(workflow.ContainsKey("202"));
        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, window.Id),
            "Saved video does not trace back to the sourced footage chain.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_stage0_pixel_upscale_scales_the_footage_before_sampling()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        // Stage 0 refines its own footage with a ×2 pixel upscale: the conformed footage is scaled
        // to the final 1024×1024 dims before stage 0's sampler (start_at_step 5) encodes it.
        ((JArray)sourced["Stages"])[0]["Upscale"] = 2.0;
        ((JArray)sourced["Stages"])[0]["UpscaleMethod"] = "pixel-lanczos";
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // The conform scale retargets in place to the ×2 dims — a single ImageScale straight from
        // the raw footage to the final dims, no chained base-dims intermediate and nothing dangling.
        ImageScaleNode scale = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Equal(window.Id, scale.Image.Connection!.Node.Id);
        Assert.Equal(1024, scale.Width.LiteralAsInt());
        Assert.Equal(1024, scale.Height.LiteralAsInt());
        // Stage 0 samples the upscaled pixels.
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Equal(5, sampler.StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, sampler, scale.Id),
            "Stage 0 sampler does not consume the upscaled footage.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Generated_t2v_clip_ic_lora_guides_are_cropped_in_real_step_order()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel icLora = new(
            loraHandler, "/tmp", "/tmp/UnitTest_IcLoraDrive.safetensors", "UnitTest_IcLoraDrive.safetensors");
        loraHandler.Models[icLora.Name] = icLora;

        // A GENERATED clip 0 in the real text-to-video step order (raw undecoded AV latent at
        // VideoStages time): every emitted IC-LoRA guide must be paired with an LTXVCropGuides
        // after its sampler — a guide that reaches a sampler the host built natively would leave
        // the guide frames in the output.
        JObject clip = MakeGeneratedClip(models);
        clip["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = "UnitTest_IcLoraDrive",
            ["Source"] = Constants.IcLoraSourceUpload,
            ["DriveMedia"] = new JObject
            {
                ["Data"] = "data:video/mp4;base64,QUJD",
                ["FileName"] = "drive.mp4",
            },
        });
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel, MakeRootConfig(512, 512, clip).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 0);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 0);
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            new[] { SeedRawTextToVideoAvLatentRootStep(), WorkflowTestHarness.CorePreVideoSavePrepStep() }
                .Concat(WorkflowTestHarness.VideoStagesSteps()),
            features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        int guides = bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count();
        int crops = bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count();
        Assert.True(guides > 0, "No IC-LoRA guide was emitted at all.");
        Assert.Equal(guides, crops);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_with_a_captured_init_image_does_not_reinject_it()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // No explicit stage ImageReference (the fixture default would set ImageRefWasExplicit and
        // mask the implicit-ref path this test guards).
        JObject sourced = MakeSourcedClip(models);
        ((JObject)((JArray)sourced["Stages"])[0]).Remove("ImageReference");
        (JObject workflow, WorkflowGenerator g) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // The host init image (Refiner ref) is captured in this flow; without the sourced-clip
        // guard in ResolveStageClipRefs the implicit image-to-video default ref would become the
        // primary guide and inplace-merge the init image into the encoded footage latent.
        Assert.True(
            g.NodeHelpers.ContainsKey("videostages.refiner.media"),
            "Harness precondition: the refiner init image was not captured.");
        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_without_duration_generates_normally()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
        sourced.Remove("Duration");
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Without a clip duration the used range is undefined; the source video is dropped at parse
        // time and the clip's stages run as usual.
        Assert.Empty(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.NotEmpty(SamplerNodesOrdered(bridge));
    }
}
