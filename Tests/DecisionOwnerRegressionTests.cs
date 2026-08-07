using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Execution.Audio;
using VideoStages.Execution.Graph;
using VideoStages.Planning;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime;
using VideoStages.Architectures.Ltx2.Runtime.Chain;

namespace VideoStages.Tests;

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

    [Theory]
    [InlineData(true, false, 512, 32)]
    [InlineData(false, true, 256, 48)]
    public void Explicit_vae_decode_tiling_uses_one_owner_for_splice_and_adhoc_paths(
        bool setSpatialTile,
        bool setTemporalTile,
        int expectedTileSize,
        int expectedTemporalSize)
    {
        WorkflowGenerator generator = Generator();
        if (setSpatialTile)
        {
            generator.UserInput.Set(T2IParamTypes.VAETileSize, 512);
        }
        if (setTemporalTile)
        {
            generator.UserInput.Set(T2IParamTypes.VAETemporalTileSize, 48);
        }

        LtxDecodeConfig config = LtxDecodeConfig.From(generator);

        Assert.True(config.UseTiledDecode);
        Assert.Equal(expectedTileSize, config.TileSize);
        Assert.Equal(expectedTemporalSize, config.TemporalSize);
        Assert.Equal(64, config.Overlap);
        Assert.Equal(4, config.TemporalOverlap);

        JObject workflow = [];
        WorkflowGenerator decodeGenerator = Generator(workflow);
        if (setSpatialTile)
        {
            decodeGenerator.UserInput.Set(T2IParamTypes.VAETileSize, 512);
        }
        if (setTemporalTile)
        {
            decodeGenerator.UserInput.Set(T2IParamTypes.VAETemporalTileSize, 48);
        }
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
        Assert.Equal(vaeId, tiled.Vae.Connection?.Node.Id);
        Assert.Equal(latentId, tiled.Samples.Connection?.Node.Id);
    }

    [Fact]
    public void Ltx2_default_decode_uses_core_tiling_geometry()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        WorkflowGenerator generator = Generator();
        generator.FinalLoadedModel = models.VideoModel;

        LtxDecodeConfig config = LtxDecodeConfig.From(generator);

        Assert.True(config.UseTiledDecode);
        Assert.Equal(2048, config.TileSize);
        Assert.Equal(256, config.Overlap);
        Assert.Equal(64, config.TemporalSize);
        Assert.Equal(16, config.TemporalOverlap);

        JObject workflow = [];
        generator.Workflow = workflow;
        string vaeId;
        string latentId;
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            vaeId = bridge.AddStub("UnitTest_Vae", "900")
                .WithOutputs(WGNodeData.DT_VAE).Id;
            latentId = bridge.AddStub("UnitTest_Latent", "901")
                .WithOutputs(WGNodeData.DT_LATENT_VIDEO).Id;
        }
        WGNodeData vae = new(new JArray(vaeId, 0), generator, WGNodeData.DT_VAE, null);
        WGNodeData latent = new(
            new JArray(latentId, 0), generator, WGNodeData.DT_LATENT_VIDEO, null);

        _ = VaeDecodePreference.AsRawImage(generator, latent, vae);

        using WorkflowBridge decoded = WorkflowBridge.Create(generator.Workflow);
        VAEDecodeTiledNode tiled = Assert.Single(
            decoded.Graph.NodesOfType<VAEDecodeTiledNode>());
        Assert.Equal(2048, tiled.TileSize.LiteralAsInt());
        Assert.Equal(256, tiled.Overlap.LiteralAsInt());
        Assert.Equal(64, tiled.TemporalSize.LiteralAsInt());
        Assert.Equal(16, tiled.TemporalOverlap.LiteralAsInt());
    }

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
    }

    [Fact]
    public void Captured_control_image_reads_as_absent_once_its_node_is_removed()
    {
        using SwarmUiTestContext testContext = new();
        WorkflowGenerator generator = GeneratorWithVideoControlNet();
        ControlNetCoreMediaCapture capture = new(generator);
        capture.Capture();
        Assert.True(capture.TryGetCapturedControlImage(0, out WGNodeData captured));
        string nodeId = captured.Path[0].Value<string>();

        using (WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow))
        {
            VideoGraphHelpers.RemoveNode(generator, bridge, nodeId);
        }

        Assert.False(capture.TryGetCapturedControlImage(0, out WGNodeData image));
        Assert.Null(image);
    }

    [Fact]
    public void InitVideo_only_timeline_runs_the_controlnet_capture_host_phase()
    {
        using SwarmUiTestContext testContext = new();
        WorkflowGenerator generator = GeneratorWithVideoControlNet();
        VideoExecutionPlan plan = InitVideoOnlyPlan();
        VideoExecutionPlanContext request = BoundContext(generator, plan);

        request.ExecutePrepared(host => host.CaptureControlNetPreprocessors());

        Assert.True(
            new ControlNetCoreMediaCapture(generator).TryGetCapturedAudio(0, out _));
    }

    [Fact]
    public void Source_only_controlnet_capture_preserves_raw_host_media()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = GeneratorWithVideoControlNet();

        VideoExecutionPlan plan = PlanWithArchitectures(NoneArchitecture.Descriptor);
        BoundContext(generator, plan).ExecutePrepared(host => host.CaptureControlNetPreprocessors());

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
        ControlNetCoreMediaCapture captures = new(generator);
        Assert.True(captures.TryGetCapturedControlImage(0, out WGNodeData raw));
        Assert.True(JToken.DeepEquals(raw.Path, new JArray(hostResize.Id, 0)));
        Assert.True(captures.TryGetCapturedAudio(0, out WGNodeData audio));
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
        VideoExecutionPlanContext request = BoundContext(generator, plan);

        request.ExecutePrepared(host => host.CaptureControlNetPreprocessors());
        request.ExecutePrepared(host => host.CaptureControlNetPreprocessors());

        Assert.True(
            new ControlNetCoreMediaCapture(generator)
                .TryGetCapturedControlImage(0, out WGNodeData raw));
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
        // ImageFromBatch defaults to exactly this slice. Core builds the wrap as a raw JObject, so
        // an unwritten input would be absent from the shipped JSON — which is the only way to tell
        // a configured slice from a bare node here.
        TypedWorkflowAssertions.AssertShippedLiteral(generator.Workflow, wrapper, "batch_index", 0);
        TypedWorkflowAssertions.AssertShippedLiteral(generator.Workflow, wrapper, "length", 1);
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
        // When LTX runs first, it is an init-video clip; the generated foreign clip owns the root.
        ClipPlan[] clips = ltxRunsFirst
            ? [InitVideoClip(0, ltx), GeneratedClip(1, foreign)]
            : [GeneratedClip(0, foreign), GeneratedClip(1, ltx)];
        VideoExecutionPlan plan = Plan(clips);
        VideoExecutionPlanContext request = BoundContext(
            generator,
            plan,
            [new ForeignRootAdapter(generator, foreign.Id), new Ltx2SessionProvider(generator)]);

        request.ExecutePrepared(host => host.CaptureControlNetPreprocessors());

        Assert.True(
            new ControlNetCoreMediaCapture(generator)
                .TryGetCapturedControlImage(0, out WGNodeData raw));
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

        Assert.True(JToken.DeepEquals(raw.Path, new JArray(hostResize.Id, 0)));
        Assert.Equal(hostResize.Id, ltxResize.Input.Connection?.Node.Id);
        Assert.True(JToken.DeepEquals(normalized.Path, new JArray(ltxResize.Id, 0)));
        Assert.Equal("400", apply.Image.Connection?.Node.Id);
        Assert.Empty(bridge.Graph.NodesOfType<ImageFromBatchNode>());
    }

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
        ClipPlan clip = new(
            0,
            48,
            ArchitectureEntryMode.ImageToVideo,
            null,
            [],
            Audio: null,
            SavesAudioTrack: false);

        Assert.Equal(2.0, ClipAudioBedDuration.Seconds(clip, 24, media));
        Assert.Equal(1.6, ClipAudioBedDuration.Seconds(clip, 0, media), 6);
        Assert.Equal(0, ClipAudioBedDuration.Seconds(clip, 0, null));
    }

    private static VideoExecutionPlan InitVideoOnlyPlan()
    {
        StagePlan[] noStages = [];
        ClipPlan clip = new(
            0,
            25,
            ArchitectureEntryMode.InitVideo,
            new("data:video/mp4;base64,QUJD", "source.mp4", 0, 512, 512, 24),
            noStages,
            Audio: null,
            SavesAudioTrack: false)
        {
            Architecture = NoneArchitecture.Descriptor,
        };
        return new(
            512,
            512,
            24,
            new(
                HostRootKind.TextToVideo,
                IgnoresHostRootOutput: true,
                UsesGeneratedClipDonor: false,
                InterceptsHostCore: true,
                UsesStageHandoff: false,
                DropsTextToVideoRootDonor: false),
            [clip],
            [],
            []);
    }

    private static WorkflowGenerator GeneratorWithVideoControlNet()
    {
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        T2IModelHandler handler = new() { ModelType = "ControlNet" };
        T2IModel model = TestStubModel.Create(handler, "UnitTest_ControlNet.safetensors");
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
                ? InitVideoClip(index, architecture)
                : GeneratedClip(index, architecture))]);

    private static VideoExecutionPlan Plan(ClipPlan[] clips)
    {
        bool firstClipHasInitVideo =
            clips.FirstOrDefault()?.EntryMode == ArchitectureEntryMode.InitVideo;
        bool hasGeneratedClip = clips.Any(clip =>
            clip.EntryMode != ArchitectureEntryMode.InitVideo);
        return new(
            512,
            512,
            24,
            new(
                HostRootKind.TextToVideo,
                IgnoresHostRootOutput: true,
                UsesGeneratedClipDonor: false,
                InterceptsHostCore: true,
                UsesStageHandoff: !firstClipHasInitVideo,
                DropsTextToVideoRootDonor: firstClipHasInitVideo && hasGeneratedClip),
            clips,
            [],
            []);
    }

    private static VideoExecutionPlanContext BoundContext(
        WorkflowGenerator generator,
        VideoExecutionPlan plan,
        IEnumerable<IArchitectureGenerationSessionProvider> providers = null)
    {
        IEnumerable<IArchitectureGenerationSessionProvider> innerProviders =
            providers
            ?? VideoArchitectureManifest.CreateProductionRuntimeProviders(
                generator,
                plan.Clips.Select(clip => clip.Architecture.Id));
        VideoArchitectureExecutionHost host = new(
            generator,
            plan,
            innerProviders.Select(provider => new LifecycleTestProvider(provider)));
        VideoExecutionPlanContext request = new(plan, () => host);
        request.PrepareRequest();
        return request;
    }

    private sealed class LifecycleTestProvider(
        IArchitectureGenerationSessionProvider inner) :
        IArchitectureGenerationSessionProvider
    {
        public ArchitectureId ArchitectureId => inner.ArchitectureId;

        public void CaptureControlNetPreprocessors(bool ownsHostRoot) =>
            inner.CaptureControlNetPreprocessors(ownsHostRoot);

        public void CaptureBaseReference(VideoExecutionPlan plan) =>
            inner.CaptureBaseReference(plan);

        public void CaptureRefinerReference(VideoExecutionPlan plan) =>
            inner.CaptureRefinerReference(plan);

        public void ApplyRootAudioMaskDimensions() =>
            inner.ApplyRootAudioMaskDimensions();

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context) =>
            inner.CreateSession(context);
    }

    private static ClipPlan InitVideoClip(
        int id,
        VideoArchitectureDescriptor architecture) => new(
        id,
        25,
        ArchitectureEntryMode.InitVideo,
        new("data:video/mp4;base64,QUJD", $"source-{id}.mp4", 0, 512, 512, 24),
        Stages: [],
        Audio: null,
        SavesAudioTrack: false)
    {
        Architecture = architecture,
    };

    private static ClipPlan GeneratedClip(
        int id,
        VideoArchitectureDescriptor architecture) => new(
        id,
        25,
        ArchitectureEntryMode.ImageToVideo,
        InitVideo: null,
        [
            new StagePlan(
                id,
                0,
                0,
                StageInputKind.RootMedia,
                IsPassthrough: false,
                ArchitecturePayload: null,
                IsIntermediateStage: false)
        ],
        Audio: null,
        SavesAudioTrack: false)
    {
        Architecture = architecture,
    };

    private static VideoArchitectureDescriptor ForeignArchitecture()
    {
        ArchitectureId id = new("unit-test-foreign");
        return new(
            id,
            "Unit Test Foreign",
            [AudioSourceKind.Native],
            [ArchitectureEntryMode.ImageToVideo],
            ArchitectureFeature.None,
            new ArchitectureBoundaryPolicy(
                new Dictionary<BoundaryJoinType, RuleDecision>()));
    }

    private sealed class ForeignRootAdapter(
        WorkflowGenerator generator,
        ArchitectureId architectureId) :
        IArchitectureGenerationSessionProvider
    {
        public ArchitectureId ArchitectureId => architectureId;

        public void CaptureControlNetPreprocessors(bool ownsHostRoot)
        {
            using WorkflowBridge bridge = BridgeSync.For(generator);
            ControlNetApplyAdvancedNode apply =
                bridge.Graph.GetNode<ControlNetApplyAdvancedNode>("308");
            UnknownNode branch = bridge.AddStub("UnitTest_ForeignControlBranch", "400")
                .WithOutputs(WGNodeData.DT_IMAGE);
            branch.GetInput("image").ConnectToUntyped(apply.Image.Connection);
            apply.Image.ConnectToUntyped(branch.GetOutput(0));
        }

        public IVideoGenerationSession CreateSession(
            ArchitectureTimelineSessionContext context) =>
            throw new NotSupportedException();
    }
}
