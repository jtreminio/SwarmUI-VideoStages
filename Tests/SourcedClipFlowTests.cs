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
        [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    private static JObject MakeSourcedClip(TestModelBundle models)
    {
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
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

        // Per-clip refine: the sourced clip's single stage is a full passthrough — no generation
        // scaffold at all, the conformed footage IS the stage output — and joins the cross-clip
        // merge directly. Only the generated clip samples.
        Assert.DoesNotContain(
            SamplerNodesOrdered(bridge),
            sampler => ReachesUpstream(bridge, sampler, window.Id));
        Assert.Contains(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>(),
            batch => ReachesUpstream(bridge, batch, window.Id));
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
        // The passthrough stage 0 emits no sampler; the refine stage consumes the footage directly.
        SwarmKSamplerNode refine = Assert.Single(
            SamplerNodesOrdered(bridge),
            s => ReachesUpstream(bridge, s, window.Id));
        Assert.Equal((int)Math.Floor(12 * 0.5), refine.StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, refine.LatentImage.Connection!.Node, window.Id),
            "Refine stage does not chain back to the passthrough footage.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_loader_only_ic_lora_applies_once_on_the_refine_stage_not_the_passthrough()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModelHandler loraHandler = new() { ModelType = "LoRA" };
        Program.T2IModelSets["LoRA"] = loraHandler;
        T2IModel icLora = new(
            loraHandler, "/tmp", "/tmp/UnitTest_IcLoraUpscaler.safetensors", "UnitTest_IcLoraUpscaler.safetensors");
        loraHandler.Models[icLora.Name] = icLora;

        JObject sourced = MakeSourcedClip(models);
        ((JArray)sourced["Stages"]).Add(
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, upscale: 2.0, steps: 12));
        sourced["IcLoras"] = new JArray(new JObject
        {
            ["Lora"] = "UnitTest_IcLoraUpscaler",
            ["Source"] = Constants.IcLoraSourceUpload,
        });
        (JObject workflow, WorkflowGenerator _generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // A stage:-1 loader-only entry must patch only the sampling stage's model — the passthrough
        // stage 0 has no sampler, so a loader there would be a dangling node.
        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.True(
            ReachesUpstream(bridge, sampler.Model.Connection!.Node, loader.Id),
            "The IC-LoRA loader does not feed the refine sampler's model.");
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_clip_pixel_upscale_retargets_the_conform_scale_instead_of_chaining()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject sourced = MakeSourcedClip(models);
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
        int windowFrames = Constants.ContinueOverlapDefaultFrames + 1;
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
        Assert.Equal(Constants.ContinueOverlapDefaultFrames + 1, ramp.Frames.LiteralAsInt());
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

        (JObject workflow, WorkflowGenerator g) = GenerateSourcedFlow(
            models, MakeSourcedClip(models));
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
        // Passthrough stage + pruned root generation: the workflow samples nothing at all.
        Assert.Empty(SamplerNodesOrdered(bridge));

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
        // a loader-only IC-LoRA, and a ×2 pixel refine stage.
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

        // The root t2v generation must be fully pruned — only the refine stage samples. A surviving
        // root sampler is a dangling chain the host post-cleanup cannot remove (SwarmKSampler is
        // not in its unused-classes allowlist).
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        SwarmKSamplerNode refine = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.True(
            ReachesUpstream(bridge, refine, window.Id),
            "The surviving sampler is not the refine stage's sampler.");
        Assert.False(workflow.ContainsKey("10"), "Root t2v sampler was left dangling.");
        Assert.False(workflow.ContainsKey("11"), "Dead root latent consumer was left dangling.");
        Assert.False(workflow.ContainsKey("104"), "Root AV concat was left dangling.");
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
