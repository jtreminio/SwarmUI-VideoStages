using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// The generic host-video fallback that drives any video model the extension has no module for.
/// <para>
/// It is selected when no specialized module claims the model. Its descriptor declares no
/// features and a frame grid of 1, so everything beyond model/steps/control/upscale is warned
/// about and dropped.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class HostVideoContractTests
{
    /// <summary>A ControlNet guide payload; <c>ValidateParam</c> rejects anything under 10 base64
    /// characters.</summary>
    private const string ControlNetVideoPayload =
        "data:video/mp4;base64,AAAAAAAAAAAAAAAAAAAAAA==";

    private static SwarmFrameWindowNode AssertSourceConformChain(
        WorkflowBridge bridge,
        int expectedStartFrame,
        int expectedFrames) =>
        TypedWorkflowAssertions.AssertSourceConformChain(
            bridge,
            expectedStartFrame,
            expectedFrames,
            VideoStagesWorkflowFixture.Width,
            VideoStagesWorkflowFixture.Height);

    /// <summary>
    /// A model with no architecture module of its own runs on SwarmUI's stock video graph, twice:
    /// each stage builds its own <c>HunyuanVideo15ImageToVideo</c> conditioning. Stage 0 samples
    /// that node's latent from core's base image; stage 1 upscales the decoded result, re-encodes
    /// it and starts halfway through its schedule.
    /// </summary>
    [Fact]
    public async Task Hunyuan_15_image_entry_runs_two_real_host_stages()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(MakeClip(
                    1.0,
                    fixture.Stage(steps: 8),
                    fixture.Stage("PreviousStage", control: 0.5, upscale: 2, steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        HunyuanVideo15ImageToVideoNode opening = Assert.Single(
            bridge.Graph.NodesOfType<HunyuanVideo15ImageToVideoNode>(),
            node => node.Width.LiteralAsInt() == 512);
        HunyuanVideo15ImageToVideoNode refining = Assert.Single(
            bridge.Graph.NodesOfType<HunyuanVideo15ImageToVideoNode>(),
            node => node.Width.LiteralAsInt() == 1024);
        Assert.Same(opening.Latent, first.LatentImage.Connection);
        Assert.Same(opening.Positive, first.Positive.Connection);
        Assert.Same(refining.Positive, second.Positive.Connection);
        Assert.Equal(25, opening.Length.LiteralAsInt());
        Assert.True(ReachesUpstream(
            bridge,
            opening.StartImage.Connection?.Node,
            BaseImage(bridge, fixture.BaseSampler(bridge)).Id));

        // The refining stage re-encodes its own upscaled frames rather than sampling the
        // conditioning node's fresh latent.
        VAEEncodeNode reEncode = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        Assert.Same(refining.StartImage.Connection, reEncode.Pixels.Connection);
        Assert.True(ReachesUpstream(bridge, reEncode, first.Id));
        Assert.Equal(5, second.StartAtStep.LiteralAsInt());
        Assert.Equal(10, second.Steps.LiteralAsInt());

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.AttachedAudio);

        live.AssertAllLive(opening, refining, reEncode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A text-entry host model samples core's own empty-latent primitive for its family — the
    /// generic runtime does not substitute a latent of its own.
    /// </summary>
    [Fact]
    public async Task Mochi_text_entry_uses_the_real_host_empty_video_primitive()
    {
        using MochiWorkflowFixture fixture = MochiWorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip(1.0, fixture.Stage(steps: 9)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyMochiLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMochiLatentVideoNode>());
        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(VideoStagesWorkflowFixture.StageSeed(0), stage.NoiseSeed.LiteralAsLong());
        Assert.Same(latent, stage.LatentImage.Connection?.Node);
        Assert.Equal(25, latent.Length.LiteralAsInt());
        Assert.Equal(9, stage.Steps.LiteralAsInt());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(25, generator.CurrentMedia.Frames);

        live.AssertAllLive(latent, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An unsupported base LTX-2 checkpoint falls to the generic runtime, which lets core build
    /// the joint audio/video latent it always builds for the family. The document's fps wins over
    /// the request's for everything in the graph, while the request keeps the value the user set.
    /// The generic runtime declares no audio support, so the audio half is sampled and then
    /// dropped: nothing decodes it and the publication is video-only.
    /// </summary>
    [Fact]
    public async Task Unsupported_Ltx2_builds_the_host_joint_latent_but_publishes_video_only()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateUnsupported();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(steps: 9)));
        document["fps"] = 24;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(document, post => post["videofps"] = 17));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXVEmptyLatentAudioNode audioLatent = Assert.Single(
            bridge.Graph.NodesOfType<LTXVEmptyLatentAudioNode>());
        LTXVConcatAVLatentNode jointLatent = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(audioLatent, jointLatent.AudioLatent.Connection?.Node);
        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(jointLatent, stage.LatentImage.Connection?.Node);

        Assert.Equal(24, audioLatent.FrameRate.LiteralAsDouble());
        Assert.Equal(
            24,
            Assert.Single(bridge.Graph.NodesOfType<LTXVConditioningNode>())
                .FrameRate.LiteralAsDouble());
        Assert.Equal(24, generator.CurrentMedia.FPS);
        Assert.Equal(24, generator.RequireVideoExecutionPlanContext().Plan.FramesPerSecond);
        Assert.Equal(17, generator.UserInput.Get(T2IParamTypes.VideoFPS));

        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Null(live.PublishedAudio());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());

        live.AssertAllLive(audioLatent, jointLatent, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A source clip on the generic runtime conforms its footage the same way every architecture
    /// does and then hands the frames to the host's own conditioning — with no audio branch at all,
    /// because the generic descriptor declares no audio source.
    /// </summary>
    [Fact]
    public async Task Generic_source_entry_uses_the_neutral_video_only_conformance_path()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(SourceClip(
                    1.0,
                    1.0,
                    fixture.Stage(control: 0.5, steps: 8)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // The window opens one second into the footage and runs the clip's whole second.
        SwarmFrameWindowNode window = AssertSourceConformChain(bridge, 24, 25);
        HunyuanVideo15ImageToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<HunyuanVideo15ImageToVideoNode>());
        Assert.True(ReachesUpstream(bridge, conditioning.StartImage.Connection?.Node, window.Id));
        Assert.Equal(25, conditioning.Length.LiteralAsInt());

        // The source replaces the host video root; core's base image pass survives it, protected by
        // core's own image save, so the stage's is the only video sampler.
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        VAEEncodeNode reEncode = Assert.IsType<VAEEncodeNode>(stage.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, reEncode, window.Id));
        Assert.Equal(4, stage.StartAtStep.LiteralAsInt());

        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Null(live.PublishedAudio());
        Assert.Equal(25, generator.CurrentMedia.Frames);

        live.AssertAllLive(window, conditioning, reEncode, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An authored audio track plays over a video model that knows nothing about audio: the mixer
    /// runs after decode, over a silent bed of the clip's own length, and the result is what the
    /// save muxes.
    /// </summary>
    [Fact]
    public async Task Generic_only_timeline_overlays_authored_audio_tracks_after_decode()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(steps: 8)));
        document["audioTracks"] = new JArray(
            AudioTrack("ignored-overlay", 1.0, "overlay.wav", AudioSpan(0, 0.5, 0)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        EmptyAudioNode bed = Assert.Single(bridge.Graph.NodesOfType<EmptyAudioNode>());
        // The bed is the clip's own decoded length: 25 frames at 24 fps.
        Assert.Equal(25 / 24d, bed.Duration.LiteralAsDouble() ?? 0, 6);
        AudioMergeNode mix = Assert.Single(bridge.Graph.NodesOfType<AudioMergeNode>());
        Assert.Same(bed, mix.Audio1.Connection?.Node);
        Assert.Same(mix, live.PublishedAudio());
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        Assert.Single(generator.GetTimelineSpec().TimelineAudioSpans);

        live.AssertAllLive(StageSampler(bridge, 0), mix);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An authored audio track stays in sync with the request's global frame trim. The overlay bed
    /// is built from the clip's planned length, so whichever order the two run in, the published
    /// audio must end up spanning the same frames the published video does.
    /// </summary>
    [Fact]
    public async Task Generic_only_timeline_keeps_authored_audio_in_sync_with_the_global_trim()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(1.0, fixture.Stage(steps: 8)));
        document["audioTracks"] = new JArray(
            AudioTrack("ignored-overlay", 1.0, "overlay.wav", AudioSpan(0, 0.5, 0)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["trimvideostartframes"] = 2;
                    post["trimvideoendframes"] = 3;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(2, trim.TrimStart.LiteralAsInt());
        Assert.Equal(3, trim.TrimEnd.LiteralAsInt());
        Assert.Equal(20, generator.CurrentMedia.Frames);

        // The bed is still the clip's planned 25 frames; the global trim takes the same 5 frames
        // off the mixed result that it takes off the video.
        EmptyAudioNode bed = Assert.Single(bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(25 / 24d, bed.Duration.LiteralAsDouble() ?? 0, 6);
        TrimAudioDurationNode audioTrim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => node.Audio.Connection?.Node is AudioMergeNode);
        Assert.Equal(2 / 24d, audioTrim.StartIndex.LiteralAsDouble() ?? 0, 6);
        Assert.Equal(20 / 24d, audioTrim.Duration.LiteralAsDouble() ?? 0, 6);
        Assert.Same(audioTrim, live.PublishedAudio());

        live.AssertAllLive(StageSampler(bridge, 0), trim);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A stage LoRA on the generic runtime loads through core's own model-gen confinement step.
    /// Hunyuan 1.5's compat class does not target the text encoder, so the authored
    /// <c>textEncoderWeight</c> is dropped and the loader is model-only. The stage's LoRAs are
    /// borrowed and handed back, so the request's LoRA list is untouched, and the borrowed host
    /// model-loader cache is dropped rather than handed on.
    /// </summary>
    [Fact]
    public async Task Generic_stage_applies_an_ordinary_LoRA_through_the_host_loader()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_HostVideo_Lora.safetensors");
        JObject stage = fixture.Stage(steps: 8);
        stage["loras"] = new JArray(new JObject
        {
            ["name"] = "UnitTest_HostVideo_Lora",
            ["weight"] = 0.45,
            ["textEncoderWeight"] = 0.2,
        });

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(MakeClip(1.0, stage))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertModelOnlyLora(
            LoraLoaderNodesOf(bridge),
            "UnitTest_HostVideo_Lora.safetensors",
            0.45);
        LoraLoaderModelOnlyNode lora = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>());
        SwarmKSamplerNode stageSampler = StageSampler(bridge, 0);
        Assert.Same(lora, stageSampler.Model.Connection?.Node);
        Assert.IsType<UNETLoaderNode>(lora.Model.Connection?.Node);
        Assert.False(generator.UserInput.TryGet(T2IParamTypes.Loras, out List<string> _));

        // Core's base-pass loader key is the control that this cache is populated at all.
        Assert.Contains(
            $"modelloader_{fixture.BaseModel.Name}_Base",
            generator.NodeHelpers.Keys);
        Assert.DoesNotContain(
            $"modelloader_{fixture.Model.Name}_image2video",
            generator.NodeHelpers.Keys);

        live.AssertAllLive(lora, stageSampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Everything the generic runtime cannot honour is warned about and left out of the graph:
    /// IC-LoRAs, a non-<c>Generated</c> stage reference, audio output/reuse/derived duration,
    /// reference framing, request-global creativity, and prompt audio/video attachments. The
    /// authored document keeps all of it. An upscale model is the exception — it is a stock ComfyUI
    /// operation, so the generic runtime drives it and says nothing.
    /// <para>
    /// A ControlNet is configured on the request too, so the stage's authored
    /// <c>controlNetStrength</c> has something it could have reached: core builds its own apply
    /// chain for the base image pass, and the claim is that neither host stage conditions on it.
    /// Nothing warns about the dropped stage strength — the generic descriptor has no ControlNet
    /// feature for <c>ArchitectureCapabilityValidator</c> to report against.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Unsupported_generic_extras_warn_and_do_not_reach_the_host_stage()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();
        UnitTestStubs.EnsureComfyControlNetParamsRegistered();
        fixture.InstallModel("ControlNet", "UnitTest_ControlNet.safetensors");
        JObject stage = fixture.Stage(
            "Base",
            control: 0.5,
            upscale: 2,
            upscaleMethod: "model-not-supported",
            steps: 8);
        stage["controlNetStrength"] = 0.8;
        stage["icLoraStrengths"] = new JArray(1.0);
        stage["frameRefStrengths"] = new JArray(0.7);
        JObject clip = MakeClip(1.0, fixture.Stage(steps: 8), stage);
        clip["saveAudioTrack"] = true;
        clip["reuseAudio"] = true;
        clip["clipLengthFromAudio"] = true;
        clip["refFraming"] = "fit";
        clip["icLoras"] = new JArray(new JObject
        {
            ["name"] = "ignored.safetensors",
            ["source"] = "Incoming",
        });

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip), post =>
                {
                    post["video2videocreativity"] = 0.2;
                    post["promptaudios"] = "data:audio/wav;base64,QUJDQUJDQUJDQUJD";
                    post["promptvideos"] = "data:video/mp4;base64,QUJDQUJDQUJDQUJD";
                    post["controlnetmodel"] = "UnitTest_ControlNet";
                    post["controlnetstrength"] = 0.8;
                    post["controlnetimageinput"] = ControlNetVideoPayload;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            [
                "effective-request.unsupported-audio-derived-duration-ignored",
                "effective-request.unsupported-audio-output-ignored",
                "effective-request.unsupported-audio-reuse-ignored",
                "effective-request.unsupported-ic-lora-ignored",
                "effective-request.unsupported-reference-framing-ignored",
                "effective-request.unsupported-stage-reference-ignored",
            ],
            Diagnostics(generator).Select(diagnostic => diagnostic.Code).Order());
        // Request-global settings warn at preflight, which never enters the compiled plan above.
        Assert.Equal(
            [
                "host-video.audio-reference.ignored",
                "host-video.creativity.ignored",
                "host-video.video-reference.ignored",
            ],
            generator.RequireVideoExecutionPlanContext()
                .PreflightDiagnostics
                .Select(diagnostic => diagnostic.Code)
                .Order());

        // Two stages ran; nothing any of the dropped settings would have built is in the graph.
        Assert.Equal(2, bridge.Graph.NodesOfType<HunyuanVideo15ImageToVideoNode>().Count);
        Assert.Empty(LoraLoaderNodesOf(bridge));
        Assert.Empty(bridge.Graph.NodesOfType<SwarmFrameImageNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Null(live.PublishedAudio());

        // Core's ControlNet conditions its own base image pass and stops there: the only video
        // upload in the graph is its guide — 'Prompt Videos' built none — and neither stage's
        // conditioning traces to the apply.
        ControlNetApplyAdvancedNode apply = Assert.Single(
            bridge.Graph.NodesOfType<ControlNetApplyAdvancedNode>());
        Assert.Single(bridge.Graph.NodesOfType<ControlNetLoaderNode>());
        SwarmLoadVideoB64Node controlSource = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.True(ReachesUpstream(bridge, apply.Image.Connection?.Node, controlSource.Id));
        // Reachability would prove nothing — every stage guides from the base image and so reaches
        // the base pass's whole conditioning branch. The claim is what each node reads directly.
        Assert.Same(apply.Positive, fixture.BaseSampler(bridge).Positive.Connection);
        Assert.All(
            bridge.Graph.NodesOfType<HunyuanVideo15ImageToVideoNode>(),
            node => Assert.IsType<CLIPTextEncodeNode>(node.PositiveInput.Connection?.Node));

        UpscaleModelLoaderNode upscaleLoader = Assert.Single(
            bridge.Graph.NodesOfType<UpscaleModelLoaderNode>());
        ImageUpscaleWithModelNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageUpscaleWithModelNode>());
        Assert.Same(upscaleLoader, upscale.UpscaleModel.Connection?.Node);
        Assert.DoesNotContain(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("upscale", StringComparison.OrdinalIgnoreCase));

        ClipSpec authored = Assert.Single(generator.GetTimelineSpec().Clips);
        Assert.True(authored.SaveAudioTrack);
        Assert.True(authored.ReuseAudio);
        Assert.True(authored.ClipLengthFromAudio);
        Assert.Equal(ReferenceFramingMode.Fit, authored.ReferenceFraming);
        Assert.Equal("Base", authored.Stages[1].ImageReference);
        Assert.Equal(0.8, authored.Stages[1].ControlNetStrength);
        Assert.Equal(0.2, generator.UserInput.Get(T2IParamTypes.Video2VideoCreativity));

        live.AssertAllLive(upscale, StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>Two host-video families loaded at once, for the mixed-compatibility refusal.</summary>
    private sealed class GenericPairFixture : VideoStagesWorkflowFixture
    {
        private GenericPairFixture()
            : base(
                [Hunyuan15WorkflowFixture.ModelFixturePath, MochiWorkflowFixture.ModelFixturePath],
                withBaseModel: true)
        {
        }

        public static GenericPairFixture Create() => new();

        public T2IModel MochiModel => Models[1];

        protected override string ClipVisionFileName =>
            TestModelFactory.Hunyuan15ClipVisionFileName;

        protected override void InstallSupportModels()
        {
            TestModelFactory.InstallHunyuan15SupportModels();
            TestModelFactory.InstallMochiSupportModels();
        }

        public override int DefaultSteps => 12;

        public override double DefaultCfgScale => 4.5;

        public override int ExpectedGeneratedFrames => RequestedFrames;
    }

    /// <summary>
    /// The generic runtime drives one host family per clip: its stages share a model loader and a
    /// conditioning primitive, so two compatibility classes in one clip cannot both be honoured.
    /// The refusal is readable, and it lands before anything is built — the shape
    /// <c>HostVideoRuntimeFlowTests.AssertRejectedBeforeMutation</c> pins.
    /// </summary>
    [Fact]
    public async Task One_clip_cannot_mix_generic_compatibility_classes()
    {
        using GenericPairFixture fixture = GenericPairFixture.Create();

        SwarmReadableErrorException error =
            await Assert.ThrowsAsync<SwarmReadableErrorException>(() =>
                ComfyWorkflowApiTestHarness.GenerateAsync(
                    fixture.ImageToVideoPost(MakeDocument(MakeClip(
                        1.0,
                        fixture.Stage(steps: 8),
                        MakeStage(
                            fixture.MochiModel.Name,
                            "PreviousStage",
                            control: 0.5,
                            steps: 8))))));

        Assert.Contains(
            "All authored stages in one clip must use one host compatibility class",
            error.Message);
    }

    /// <summary>
    /// The request-global video-swap and creativity settings are SwarmUI's own; the generic runtime
    /// refuses to act on them and says so, but must not rewrite the request either — the authored
    /// values survive for metadata. Core's video pass therefore starts at step 0, which is what the
    /// mid-generation probe reads before the extension prunes that pass.
    /// </summary>
    [Fact]
    public async Task Generic_core_pass_ignores_legacy_swap_and_creativity()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();
        JToken coreStartStep = null;
        WorkflowGenerator.WorkflowGenStep inspectCore = new(g =>
        {
            using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
            // Core's video pass, not its base image pass: at this point they are the only two
            // samplers and the base one carries the request seed. Read off the raw workflow — 0 is
            // the generated node's default, so a missing key would read back as the wanted value.
            coreStartStep = ShippedInput(
                g.Workflow,
                Assert.Single(
                    bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
                    sampler => sampler.NoiseSeed.LiteralAsLong()
                        != VideoStagesWorkflowFixture.Seed),
                "start_at_step");
        }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(1.0, fixture.Stage(steps: 8))),
                    post =>
                    {
                        post["videoswapmodel"] = fixture.Model.Name;
                        post["videoswappercent"] = 0.3;
                        post["video2videocreativity"] = 0.25;
                    }),
                extraSteps: [inspectCore]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(0, coreStartStep?.Value<int>());
        Assert.Equal(fixture.Model.Name, generator.UserInput.Get(T2IParamTypes.VideoSwapModel).Name);
        Assert.Equal(0.3, generator.UserInput.Get(T2IParamTypes.VideoSwapPercent));
        Assert.Equal(0.25, generator.UserInput.Get(T2IParamTypes.Video2VideoCreativity));
        Assert.Contains(
            Diagnostics(generator),
            diagnostic => diagnostic.Code == "effective-request.video-swap-ignored");

        // Core's swapped second pass did not survive into the timeline's graph.
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        AssertShippedLiteral(workflow, stage, "start_at_step", 0);

        live.AssertAllLive(fixture.BaseSampler(bridge), stage);
        AssertShippable(bridge, workflow, live);
    }
}
