using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.None;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Regressions for defects that existed because two modules independently decided the same thing.
/// Each test names the decision and the single owner it now belongs to.
/// </summary>
[Collection("VideoStagesTests")]
public class DecisionOwnerRegressionTests
{
    public DecisionOwnerRegressionTests() => NodeRegistrations.EnsureRegistered();

    private static WorkflowGenerator Generator(JObject workflow = null) => new()
    {
        UserInput = new T2IParamInput(null),
        Features = [],
        ModelFolderFormat = "/",
        Workflow = workflow ?? [],
    };

    // ---- 4a: one owner for VAE decode tiling geometry -------------------------------------

    [Fact]
    public void Vae_decode_tiling_uses_one_owner_for_both_splice_and_adhoc_paths()
    {
        WorkflowGenerator generator = Generator();
        generator.UserInput.Set(T2IParamTypes.VAETileSize, 512);

        // Only VAETileSize is set - the common case. The post-chain rebuilder used to fall back to
        // TemporalSize 4096 ("no temporal tiling") while VaeDecodePreference used 32, so a stage's
        // decode geometry depended purely on which builder produced it.
        LtxDecodeConfig config = LtxDecodeConfig.From(generator);

        Assert.True(config.UseTiledDecode);
        Assert.Equal(512, config.TileSize);
        Assert.Equal(LtxDecodeDefaults.TemporalSize, config.TemporalSize);
        Assert.Equal(32, config.TemporalSize);
        Assert.Equal(64, config.Overlap);
        Assert.Equal(4, config.TemporalOverlap);

        JObject workflow = [];
        WorkflowGenerator decodeGenerator = Generator(workflow);
        decodeGenerator.UserInput.Set(T2IParamTypes.VAETileSize, 512);
        string vaeId;
        string latentId;
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            vaeId = bridge.AddStub("UnitTest_Vae", "900")
                .WithOutputs(WGNodeData.DT_VAE).Id;
            latentId = bridge.AddStub("UnitTest_Latent", "901")
                .WithOutputs(WGNodeData.DT_LATENT_VIDEO).Id;
        }
        WGNodeData vae = new(new JArray(vaeId, 0), decodeGenerator, WGNodeData.DT_VAE, null);
        WGNodeData latent = new(
            new JArray(latentId, 0), decodeGenerator, WGNodeData.DT_LATENT_VIDEO, null);

        _ = VaeDecodePreference.AsRawImage(decodeGenerator, latent, vae);

        using WorkflowBridge decoded = WorkflowBridge.Create(decodeGenerator.Workflow);
        VAEDecodeTiledNode tiled = Assert.Single(
            decoded.Graph.NodesOfType<VAEDecodeTiledNode>());
        Assert.Equal(config.TileSize, tiled.TileSize.LiteralAsInt());
        Assert.Equal(config.TemporalSize, tiled.TemporalSize.LiteralAsInt());
        Assert.Equal(config.Overlap, tiled.Overlap.LiteralAsInt());
        Assert.Equal(config.TemporalOverlap, tiled.TemporalOverlap.LiteralAsInt());
    }

    // ---- 4c: one owner for the node-helper cache -------------------------------------------

    [Fact]
    public void Node_helper_invalidation_understands_every_encoding_videostages_writes()
    {
        Dictionary<string, string> nodeHelpers = new()
        {
            // Pipe-delimited StageRefStore marker.
            ["videostages.arch.ltx2.stage-ref.generated.media"] =
                "103|0|VIDEO|512|512|97|24|ltxv2",
            // JArray [nodeId, slot] ControlNet capture key.
            ["videostages.controlnet.fullimage.0"] = new JArray("103", 1).ToString(
                Formatting.None),
            // SwarmUI's own bare-node-id convention.
            ["__generic_node__UnitTest___{}"] = "103",
            // Same encodings pointing at a surviving node.
            ["videostages.arch.ltx2.stage-ref.base.media"] =
                "104|0|VIDEO|512|512|97|24|ltxv2",
            ["videostages.controlnet.audio.1"] = new JArray("104", 0).ToString(Formatting.None),
            // Not a node reference: the pre-core id snapshot.
            ["videostages.arch.ltx2.pre-core-node-ids"] = "103,104,105",
        };

        VideoGraphHelpers.InvalidateForRemovedNodes(nodeHelpers, ["103"]);

        Assert.False(nodeHelpers.ContainsKey(
            "videostages.arch.ltx2.stage-ref.generated.media"));
        Assert.False(nodeHelpers.ContainsKey("videostages.controlnet.fullimage.0"));
        Assert.False(nodeHelpers.ContainsKey("__generic_node__UnitTest___{}"));
        Assert.True(nodeHelpers.ContainsKey(
            "videostages.arch.ltx2.stage-ref.base.media"));
        Assert.True(nodeHelpers.ContainsKey("videostages.controlnet.audio.1"));
        Assert.Equal(
            "103,104,105",
            nodeHelpers["videostages.arch.ltx2.pre-core-node-ids"]);
    }

    [Fact]
    public void Captured_control_image_reads_as_absent_once_its_node_is_removed()
    {
        JObject workflow = [];
        WorkflowGenerator generator = Generator(workflow);
        string nodeId;
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            nodeId = bridge.AddStub("UnitTest_ControlImage", "310")
                .WithOutputs(WGNodeData.DT_IMAGE).Id;
        }
        VideoGraphHelpers.CachePath(
            generator, ControlNetCaptureKeys.Image(0), new JArray(nodeId, 0));
        Assert.True(ControlNetCoreMediaCapture.TryGetCapturedControlImage(generator, 0, out _));

        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VideoGraphHelpers.RemoveNode(generator, bridge, nodeId);
        }

        // The self-check the five other readers already had: a dangling capture must not be handed
        // out as a live node reference.
        Assert.False(
            ControlNetCoreMediaCapture.TryGetCapturedControlImage(
                generator, 0, out WGNodeData image));
        Assert.Null(image);
        Assert.False(generator.NodeHelpers.ContainsKey(ControlNetCaptureKeys.Image(0)));
    }

    // ---- 4d: sourced-only timelines participate in the ControlNet capture phase ------------

    [Fact]
    public void Sourced_only_timeline_runs_the_controlnet_capture_host_phase()
    {
        WorkflowGenerator generator = Generator();
        // Stale captures from a previous pass; running the capture re-evaluates and clears them.
        generator.NodeHelpers[ControlNetCaptureKeys.Image(0)] = new JArray("1", 0).ToString(
            Formatting.None);
        generator.NodeHelpers[ControlNetCaptureKeys.Audio(0)] = new JArray("1", 1).ToString(
            Formatting.None);
        VideoArchitectureExecutionHost host = new(generator);

        host.DispatchHostPhase(
            ArchitectureHostPhase.CaptureControlNetPreprocessors,
            SourcedOnlyPlan());

        // Common orchestration owns capture, so source-only execution gets its audio facts without
        // pretending that the None adapter has architecture-specific host work.
        Assert.False(generator.NodeHelpers.ContainsKey(ControlNetCaptureKeys.Image(0)));
        Assert.False(generator.NodeHelpers.ContainsKey(ControlNetCaptureKeys.Audio(0)));
    }

    [Fact]
    public void Source_only_controlnet_capture_preserves_raw_host_media()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = GeneratorWithVideoControlNet();

        new VideoArchitectureExecutionHost(generator).DispatchHostPhase(
            ArchitectureHostPhase.CaptureControlNetPreprocessors,
            PlanWithArchitectures(NoneArchitecture.Descriptor));

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        ResizeImageMaskNodeNode hostResize =
            bridge.Graph.GetNode<ResizeImageMaskNodeNode>("304");
        ControlNetApplyAdvancedNode apply =
            bridge.Graph.GetNode<ControlNetApplyAdvancedNode>("308");

        Assert.NotNull(hostResize);
        Assert.NotNull(apply);
        Assert.Equal(8, hostResize.ExtraInputs["resize_type.multiple"]?.Value<int>());
        Assert.Equal(hostResize.Id, apply.Image.Connection?.Node.Id);
        Assert.Empty(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.True(
            ControlNetCoreMediaCapture.TryGetCapturedControlImage(
                generator,
                0,
                out WGNodeData raw));
        Assert.True(JToken.DeepEquals(raw.Path, new JArray(hostResize.Id, 0)));
        Assert.True(
            new ControlNetAudioCapture(generator).TryGetCapturedAudio(
                0,
                out WGNodeData audio));
        Assert.True(JToken.DeepEquals(audio.Path, new JArray("301", 1)));
        Assert.False(
            new LtxControlNetMediaNormalizer(generator)
                .TryGetNormalizedControlImage(
                    0,
                    out WGNodeData unusedNormalized));
        Assert.Null(unusedNormalized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_timeline_ltx_normalizes_captured_media_once(
        bool ltxFirst)
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = GeneratorWithVideoControlNet();
        VideoArchitectureDescriptor[] architectures = ltxFirst
            ? [Ltx2ArchitectureModule.Instance.Descriptor, NoneArchitecture.Descriptor]
            : [NoneArchitecture.Descriptor, Ltx2ArchitectureModule.Instance.Descriptor];
        VideoArchitectureExecutionHost host = new(generator);
        VideoExecutionPlan plan = PlanWithArchitectures(architectures);

        host.DispatchHostPhase(
            ArchitectureHostPhase.CaptureControlNetPreprocessors,
            plan);
        host.DispatchHostPhase(
            ArchitectureHostPhase.CaptureControlNetPreprocessors,
            plan);

        Assert.True(
            ControlNetCoreMediaCapture.TryGetCapturedControlImage(
                generator,
                0,
                out WGNodeData raw));
        LtxControlNetMediaNormalizer normalizer = new(generator);
        Assert.True(
            normalizer.TryGetNormalizedControlImage(
                0,
                out WGNodeData normalized));
        Assert.True(normalizer.TryCreateFrameCount(0, out JArray frames));

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        ResizeImageMaskNodeNode hostResize =
            bridge.Graph.GetNode<ResizeImageMaskNodeNode>("304");
        ResizeImageMaskNodeNode ltxResize = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>(),
            node => node.Id != hostResize.Id);
        ControlNetApplyAdvancedNode apply =
            bridge.Graph.GetNode<ControlNetApplyAdvancedNode>("308");
        ImageFromBatchNode wrapper = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>());

        Assert.True(JToken.DeepEquals(raw.Path, new JArray(hostResize.Id, 0)));
        Assert.True(JToken.DeepEquals(normalized.Path, new JArray(ltxResize.Id, 0)));
        Assert.Equal(8, hostResize.ExtraInputs["resize_type.multiple"]?.Value<int>());
        Assert.Equal(64, ltxResize.ExtraInputs["resize_type.multiple"]?.Value<int>());
        Assert.Equal(hostResize.Id, ltxResize.Input.Connection?.Node.Id);
        Assert.Equal(0, wrapper.BatchIndex.LiteralAsInt());
        Assert.Equal(1, wrapper.Length.LiteralAsInt());
        Assert.Equal(ltxResize.Id, wrapper.Image.Connection?.Node.Id);
        Assert.Equal(wrapper.Id, apply.Image.Connection?.Node.Id);

        GetImageSizeNode size = Assert.Single(
            bridge.Graph.NodesOfType<GetImageSizeNode>());
        Assert.Equal(ltxResize.Id, size.Image.Connection?.Node.Id);
        Assert.True(JToken.DeepEquals(
            frames,
            WorkflowBridge.ToPath(size.BatchSize)));
    }

    // ---- 4f: one clip-audio bed duration rule ----------------------------------------------

    [Fact]
    public void Clip_audio_bed_duration_prefers_plan_fps_over_installed_media_fps()
    {
        JObject workflow = [];
        WorkflowGenerator generator = Generator(workflow);
        string mediaId;
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            mediaId = bridge.AddStub("UnitTest_Media", "500")
                .WithOutputs(WGNodeData.DT_VIDEO).Id;
        }
        WGNodeData media = new(
            new JArray(mediaId, 0), generator, WGNodeData.DT_VIDEO, null)
        {
            FPS = 30,
        };
        ClipPlan clip = new(0, 48, ClipInputKind.SourceVideo, true, null, [], Audio: null);

        // The sourced path used to read media fps only, so a resampled sourced clip placed its
        // segments against a different bed than the same clip with stages.
        Assert.Equal(2.0, ClipAudioBedDuration.Seconds(clip, 24, media));
        Assert.Equal(1.6, ClipAudioBedDuration.Seconds(clip, 0, media), 6);
        Assert.Equal(0, ClipAudioBedDuration.Seconds(clip, 0, null));
    }

    private static VideoExecutionPlan SourcedOnlyPlan()
    {
        StagePlan[] noStages = [];
        ClipPlan clip = new(
            0,
            25,
            ClipInputKind.SourceVideo,
            IsSourced: true,
            new("data", "source.mp4", 0, 512, 512, 24),
            noStages,
            Audio: null)
        {
            Architecture = NoneArchitecture.Descriptor,
        };
        return new(
            512,
            512,
            24,
            new(
                HostRootKind.TextToVideoRoot,
                RootUse.Discard,
                HostCoreDisposition.Handoff,
                TimelineOutputDisposition.PublishTimelineOutput,
                NativeAudioDisposition.DiscardWithRoot),
            [clip],
            [],
            []);
    }

    private static WorkflowGenerator GeneratorWithVideoControlNet()
    {
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        T2IModelHandler handler = new() { ModelType = "ControlNet" };
        T2IModel model = new(
            handler,
            "/tmp",
            "/tmp/UnitTest_ControlNet.safetensors",
            "UnitTest_ControlNet.safetensors");
        WorkflowGenerator generator = Generator();
        generator.UserInput.Set(T2IParamTypes.Controlnets[0].Strength, 0.8);
        generator.UserInput.Set(T2IParamTypes.Controlnets[0].Model, model);

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        SwarmLoadVideoB64Node load =
            bridge.AddNode(new SwarmLoadVideoB64Node().With(
                VideoBase64: "unit-test-video"), "300");
        GetVideoComponentsNode components =
            bridge.AddNode(new GetVideoComponentsNode(), "301");
        components.Video.ConnectTo(load.VIDEO);
        UnknownNode preprocessor = bridge.AddStub(
            "UnitTestPreprocessor",
            "303").WithOutputs(WGNodeData.DT_IMAGE);
        preprocessor.GetInput("image").ConnectToUntyped(components.Images);
        ResizeImageMaskNodeNode resize = new()
        {
            ExtraInputs = new JObject
            {
                ["resize_type.multiple"] = 8,
            },
        };
        resize.With(
            ResizeType: "scale to multiple",
            ScaleMethod: "lanczos");
        resize.Input.ConnectToUntyped(preprocessor.GetOutput(0));
        bridge.AddNode(resize, "304");
        ControlNetLoaderNode loader =
            bridge.AddNode(new ControlNetLoaderNode().With(
                ControlNetName: model.ToString(
                    generator.ModelFolderFormat)), "305");
        UnknownNode positive = bridge.AddStub(
            "UnitTest_Positive",
            "306").WithOutputs("CONDITIONING");
        UnknownNode negative = bridge.AddStub(
            "UnitTest_Negative",
            "307").WithOutputs("CONDITIONING");
        ControlNetApplyAdvancedNode apply = new();
        apply.With(Strength: 0.8, StartPercent: 0, EndPercent: 1);
        apply.PositiveInput.ConnectToUntyped(positive.GetOutput(0));
        apply.NegativeInput.ConnectToUntyped(negative.GetOutput(0));
        apply.ControlNet.ConnectTo(loader.CONTROLNET);
        apply.Image.ConnectToUntyped(resize.Resized);
        bridge.AddNode(apply, "308");
        return generator;
    }

    private static VideoExecutionPlan PlanWithArchitectures(
        params VideoArchitectureDescriptor[] architectures)
    {
        ClipPlan[] clips = [.. architectures.Select(
            (architecture, index) => new ClipPlan(
                index,
                25,
                architecture.Id == NoneArchitecture.Id
                    ? ClipInputKind.SourceVideo
                    : ClipInputKind.RootMedia,
                IsSourced: architecture.Id == NoneArchitecture.Id,
                SourceVideo: architecture.Id == NoneArchitecture.Id
                    ? new("data", $"source-{index}.mp4", 0, 512, 512, 24)
                    : null,
                Stages: [],
                Audio: null)
            {
                Architecture = architecture,
            })];
        return new(
            512,
            512,
            24,
            new(
                HostRootKind.TextToVideoRoot,
                RootUse.Discard,
                HostCoreDisposition.Handoff,
                TimelineOutputDisposition.PublishTimelineOutput,
                NativeAudioDisposition.DiscardWithRoot),
            clips,
            [],
            []);
    }
}
