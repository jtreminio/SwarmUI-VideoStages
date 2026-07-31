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
    public void Node_helper_invalidation_understands_every_supported_reference_encoding()
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
            // SwarmUI model-loader tuples can reference three independent nodes. Node ids are
            // deliberately non-numeric; only their paired output slots are integers.
            ["modelloader_removed-model_image2video"] =
                "removed-model:0:surviving-clip:1:surviving-vae:2",
            ["modelloader_removed-clip_image2video"] =
                "surviving-model:0:removed-clip:1:surviving-vae:2",
            ["modelloader_removed-vae_image2video"] =
                "surviving-model:0:surviving-clip:1:removed-vae:2",
            ["modelloader_removed-model-no-vae_image2video"] =
                "removed-model:0:surviving-clip:1::",
            ["modelloader_all-surviving-no-vae_image2video"] =
                "surviving-model:0:surviving-clip:1::",
            ["modelloader_all-surviving_image2video"] =
                "surviving-model:0:surviving-clip:1:surviving-vae:2",
            // Exact key prefix but malformed tuples must remain opaque.
            ["modelloader_missing-model_image2video"] =
                ":0:surviving-clip:1:surviving-vae:2",
            ["modelloader_partial-clip_image2video"] =
                "surviving-model:0:removed-clip::surviving-vae:2",
            ["modelloader_missing-clip_image2video"] =
                "surviving-model:0::::",
            ["modelloader_partial-vae_image2video"] =
                "surviving-model:0:surviving-clip:1::2",
            ["modelloader_bad-slot_image2video"] =
                "removed-model:not-a-slot:surviving-clip:1:surviving-vae:2",
            ["modelloader_wrong-part-count_image2video"] =
                "removed-model:0:surviving-clip:1",
            // A colon tuple under any other key is not SwarmUI's model-loader cache.
            ["videostages.opaque.colon-state"] =
                "removed-model:0:surviving-clip:1:surviving-vae:2",
        };

        VideoGraphHelpers.InvalidateForRemovedNodes(
            nodeHelpers,
            ["103", "removed-model", "removed-clip", "removed-vae"]);

        Assert.False(nodeHelpers.ContainsKey(
            "videostages.arch.ltx2.stage-ref.generated.media"));
        Assert.False(nodeHelpers.ContainsKey("videostages.controlnet.fullimage.0"));
        Assert.False(nodeHelpers.ContainsKey("__generic_node__UnitTest___{}"));
        Assert.False(nodeHelpers.ContainsKey("modelloader_removed-model_image2video"));
        Assert.False(nodeHelpers.ContainsKey("modelloader_removed-clip_image2video"));
        Assert.False(nodeHelpers.ContainsKey("modelloader_removed-vae_image2video"));
        Assert.False(nodeHelpers.ContainsKey("modelloader_removed-model-no-vae_image2video"));
        Assert.True(nodeHelpers.ContainsKey(
            "videostages.arch.ltx2.stage-ref.base.media"));
        Assert.True(nodeHelpers.ContainsKey("videostages.controlnet.audio.1"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_all-surviving-no-vae_image2video"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_all-surviving_image2video"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_missing-model_image2video"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_partial-clip_image2video"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_missing-clip_image2video"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_partial-vae_image2video"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_bad-slot_image2video"));
        Assert.True(nodeHelpers.ContainsKey("modelloader_wrong-part-count_image2video"));
        Assert.True(nodeHelpers.ContainsKey("videostages.opaque.colon-state"));
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
        VideoExecutionPlan plan = SourcedOnlyPlan();
        VideoArchitectureExecutionHost host = BoundHost(generator, plan);

        host.DispatchHostPhase(ArchitectureHostPhase.CaptureControlNetPreprocessors);

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

        VideoExecutionPlan plan = PlanWithArchitectures(NoneArchitecture.Descriptor);
        BoundHost(generator, plan).DispatchHostPhase(
            ArchitectureHostPhase.CaptureControlNetPreprocessors);

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
        VideoExecutionPlan plan = PlanWithArchitectures(architectures);
        VideoArchitectureExecutionHost host = BoundHost(generator, plan);

        host.DispatchHostPhase(ArchitectureHostPhase.CaptureControlNetPreprocessors);
        host.DispatchHostPhase(ArchitectureHostPhase.CaptureControlNetPreprocessors);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ltx_normalizes_the_capture_and_leaves_a_foreign_root_untouched(bool ltxRunsFirst)
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = GeneratorWithVideoControlNet();
        VideoArchitectureDescriptor foreign = ForeignArchitecture();
        VideoArchitectureDescriptor ltx = Ltx2ArchitectureModule.Instance.Descriptor;
        // Host phases run in clip order, so LTX runs first only as a sourced clip: the root belongs
        // to the foreign generated clip either way.
        ClipPlan[] clips = ltxRunsFirst
            ? [SourcedClip(0, ltx), GeneratedClip(1, foreign)]
            : [GeneratedClip(0, foreign), GeneratedClip(1, ltx)];
        VideoExecutionPlan plan = Plan(clips);
        VideoArchitectureExecutionHost host = BoundHost(
            generator,
            plan,
            [new ForeignRootAdapter(generator, foreign.Id), new Ltx2ExecutionAdapter(generator)]);

        host.DispatchHostPhase(ArchitectureHostPhase.CaptureControlNetPreprocessors);

        Assert.True(
            ControlNetCoreMediaCapture.TryGetCapturedControlImage(
                generator,
                0,
                out WGNodeData raw));
        Assert.True(
            new LtxControlNetMediaNormalizer(generator).TryGetNormalizedControlImage(
                0,
                out WGNodeData normalized));

        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        ResizeImageMaskNodeNode hostResize =
            bridge.Graph.GetNode<ResizeImageMaskNodeNode>("304");
        ResizeImageMaskNodeNode ltxResize = Assert.Single(
            bridge.Graph.NodesOfType<ResizeImageMaskNodeNode>(),
            node => node.Id != hostResize.Id);
        ControlNetApplyAdvancedNode apply =
            bridge.Graph.GetNode<ControlNetApplyAdvancedNode>("308");

        // Both adapters read the same immutable capture, whichever ran first.
        Assert.True(JToken.DeepEquals(raw.Path, new JArray(hostResize.Id, 0)));
        Assert.Equal(hostResize.Id, ltxResize.Input.Connection?.Node.Id);
        // LTX still caches its own normalized branch while inactive on the root.
        Assert.True(JToken.DeepEquals(normalized.Path, new JArray(ltxResize.Id, 0)));
        // The root it does not own keeps the foreign adapter's branch and no LTX frame wrapper.
        Assert.Equal("400", apply.Image.Connection?.Node.Id);
        Assert.Empty(bridge.Graph.NodesOfType<ImageFromBatchNode>());
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
        params VideoArchitectureDescriptor[] architectures) =>
        Plan([.. architectures.Select((architecture, index) =>
            architecture.Id == NoneArchitecture.Id
                ? SourcedClip(index, architecture)
                : GeneratedClip(index, architecture))]);

    private static VideoExecutionPlan Plan(ClipPlan[] clips) => new(
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

    private static VideoArchitectureExecutionHost BoundHost(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionFactoryProvider> providers = null)
    {
        IEnumerable<IArchitectureGenerationSessionFactoryProvider> innerProviders =
            providers
            ?? VideoArchitectureManifest.CreateProductionRuntimeProviders(
                generator,
                plan.Clips.Select(clip => clip.Architecture.Id));
        VideoArchitectureExecutionHost host = new(
            generator,
            plan,
            innerProviders.Select(provider => new HostPhaseTestProvider(provider)));
        VideoExecutionPlanContext request = new(plan, _ => host);
        request.PrepareRequest();
        return request.RequirePreparedExecutionHost();
    }

    private sealed class HostPhaseTestProvider(
        IArchitectureGenerationSessionFactoryProvider inner) :
        IArchitectureGenerationSessionFactoryProvider,
        IArchitectureHostPhaseParticipant,
        IArchitectureRootMediaResizerProvider
    {
        public ArchitectureId ArchitectureId => inner.ArchitectureId;

        public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
        {
            if (inner is IArchitectureHostPhaseParticipant participant)
            {
                participant.ExecuteHostPhase(context);
            }
        }

        public IArchitectureGenerationSessionFactory CreateFactory() =>
            inner.CreateFactory();

        public IArchitectureRootMediaResizer CreateRootMediaResizer() =>
            (inner as IArchitectureRootMediaResizerProvider)?.CreateRootMediaResizer();
    }

    private static ClipPlan SourcedClip(
        int id,
        VideoArchitectureDescriptor architecture) => new(
        id,
        25,
        ClipInputKind.SourceVideo,
        IsSourced: true,
        new("data", $"source-{id}.mp4", 0, 512, 512, 24),
        Stages: [],
        Audio: null)
    {
        Architecture = architecture,
    };

    /// <summary>Root media plus a stage: the shape <see cref="ArchitectureRootOwnerResolver"/>
    /// accepts as a host-root owner.</summary>
    private static ClipPlan GeneratedClip(
        int id,
        VideoArchitectureDescriptor architecture) => new(
        id,
        25,
        ClipInputKind.RootMedia,
        IsSourced: false,
        SourceVideo: null,
        [
            new StagePlan(
                id,
                0,
                0,
                StageInputKind.RootMedia,
                IsPassthrough: false,
                ArchitecturePayload: null,
                new(
                    IsTimelineTerminal: true,
                    IntermediateOutputPolicy.NotEligible,
                    PreserveConfiguredAudioTrackSave: false))
        ],
        Audio: null)
    {
        Architecture = architecture,
    };

    private static VideoArchitectureDescriptor ForeignArchitecture()
    {
        ArchitectureId id = new("unit-test-foreign");
        ModelProfileId profileId = new("unit-test-foreign-profile");
        return new(
            id,
            "Unit Test Foreign",
            profileId,
            [AudioSourceKind.Native],
            [new(
                profileId,
                profileId.Value,
                [ArchitectureEntryMode.ImageToVideo])],
            new(
                ArchitectureCapability.GeneratedEntry,
                ClipCapability.None,
                StageCapability.ImageInput),
            new ArchitectureBoundaryPolicy(
                new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>()));
    }

    /// <summary>
    /// Stands in for a future architecture that owns the host root and normalizes the shared
    /// ControlNet apply input its own way.
    /// </summary>
    private sealed class ForeignRootAdapter(
        WorkflowGenerator generator,
        ArchitectureId architectureId) :
        IArchitectureGenerationSessionFactoryProvider,
        IArchitectureHostPhaseParticipant
    {
        public ArchitectureId ArchitectureId => architectureId;

        public void ExecuteHostPhase(ArchitectureHostPhaseContext context)
        {
            using WorkflowBridge bridge = BridgeSync.For(generator);
            ControlNetApplyAdvancedNode apply =
                bridge.Graph.GetNode<ControlNetApplyAdvancedNode>("308");
            UnknownNode branch = bridge.AddStub("UnitTest_ForeignControlBranch", "400")
                .WithOutputs(WGNodeData.DT_IMAGE);
            branch.GetInput("image").ConnectToUntyped(apply.Image.Connection);
            apply.Image.ConnectToUntyped(branch.GetOutput(0));
        }

        public IArchitectureGenerationSessionFactory CreateFactory() =>
            throw new NotSupportedException();
    }
}
