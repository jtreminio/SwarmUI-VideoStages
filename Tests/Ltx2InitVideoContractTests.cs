using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Clips that refine uploaded footage instead of generating it — the conform chain, what its stages
/// do with it, how it joins generated clips, and what a clip that only publishes footage produces —
/// all generated through the real Comfy API POST path.
/// <para>
/// A sourced clip 0 makes the request abandon its own video root, but not core's base image pass:
/// core saves that image itself (node <c>30</c>, because the request has a follow-up video model),
/// and an output node protects its whole upstream from the extension's displaced-root cleanup. So
/// even a timeline that samples nothing still ships core's base pass, and "the timeline samples
/// nothing" is asserted as "the only sampler is core's". Core's LTX video root never survives,
/// which is what keeps
/// <see cref="TypedWorkflowAssertions.StageSampler"/> unambiguous here despite core's
/// image-to-video sampler sharing stage 0's seed. The one shape where it does survive is a sourced
/// stage 0 with an explicit <c>Generated</c> reference, covered in
/// <c>Ltx2GuideAndRetakeContractTests</c> — no test anywhere covers a sourced stage 0 carrying any
/// other explicit reference.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class Ltx2InitVideoContractTests
{
    private const double ClipDuration = 0.6;

    private const double StartSeconds = 1.0;

    /// <summary>The timeline rate, as a divisor for the frame-to-seconds maths below.</summary>
    private const double Fps = VideoStagesWorkflowFixture.Fps;

    /// <summary>0.6s at the timeline fps, snapped up to LTX's 8k+1 grid because the clip is
    /// sampled.</summary>
    private const int SampledFrames = 17;

    /// <summary>The same 0.6s with nothing to sample: no grid to snap to, so the raw count stands.</summary>
    private const int PassthroughFrames = 16;

    /// <summary><see cref="StartSeconds"/> at the timeline fps.</summary>
    private const int StartFrame = (int)(StartSeconds * VideoStagesWorkflowFixture.Fps);

    /// <summary>The frames a continue seam ramps over: the boundary's default overlap of 8, plus
    /// the shared frame at the join.</summary>
    private const int BlendFrames = 9;

    /// <summary>
    /// A clip driven by uploaded footage. Its stages carry no image reference: an explicit one would
    /// keep core's own video root alive as a guide donor, which is a different subject.
    /// </summary>
    private static JObject SourcedClip(params JObject[] stages) =>
        SourceClip(ClipDuration, StartSeconds, stages);

    private static JObject SourcedStage(Ltx2WorkflowFixture fixture, double control = 0.5)
    {
        JObject stage = fixture.Stage(control: control, steps: 10);
        stage.Remove("imageReference");
        return stage;
    }

    /// <summary>The two document shapes that publish footage without sampling it.</summary>
    private static JObject UnsampledSourcedClip(Ltx2WorkflowFixture fixture, bool withPassthroughStage) =>
        withPassthroughStage
            ? SourcedClip(SourcedStage(fixture, control: 0.0))
            : SourcedClip();

    private static JObject GeneratedClip(Ltx2WorkflowFixture fixture) =>
        MakeClip(ClipDuration, fixture.Stage(control: 0.5, steps: 10));

    private static JObject IcLora(
        Ltx2WorkflowFixture fixture,
        IcLoraDriveData driveData,
        string driveSource,
        string lora = "UnitTest_IcLoraDrive",
        int? stage = null,
        JObject driveMedia = null,
        string preset = null)
    {
        fixture.InstallModel("LoRA", $"{lora}.safetensors");
        JObject entry = new()
        {
            ["lora"] = lora,
            ["driveSource"] = driveSource,
            ["driveData"] = $"{driveData}",
        };
        if (stage is not null)
        {
            entry["stage"] = stage.Value;
        }
        if (driveMedia is not null)
        {
            entry["driveMedia"] = driveMedia;
        }
        if (preset is not null)
        {
            entry["preset"] = preset;
        }
        return entry;
    }

    /// <summary>The scale that fits the windowed footage to the clip's resolution.</summary>
    private static ImageScaleNode ConformScaleOf(WorkflowBridge bridge, SwarmFrameWindowNode window) =>
        Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => ReferenceEquals(scale.Image.Connection?.Node, window));

    /// <summary>
    /// The upload is decoded once, resampled to the timeline fps, windowed to the authored slice and
    /// fitted to the clip resolution; its audio is trimmed to the same slice.
    /// </summary>
    private static SwarmFrameWindowNode AssertConformChain(
        WorkflowBridge bridge,
        int expectedFrames,
        int expectedWidth = VideoStagesWorkflowFixture.Width,
        int expectedHeight = VideoStagesWorkflowFixture.Height)
    {
        SwarmFrameWindowNode window = AssertSourceConformChain(
            bridge, StartFrame, expectedFrames, expectedWidth, expectedHeight);
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());

        TrimAudioDurationNode trim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => ReferenceEquals(node.Audio.Connection, components.Audio));
        Assert.Equal(StartSeconds, trim.StartIndex.LiteralAsDouble());
        Assert.Equal(
            expectedFrames / Fps,
            trim.Duration.LiteralAsDouble()!.Value,
            precision: 6);

        return window;
    }

    /// <summary>
    /// The timeline contributed no sampler of its own. Core's base image pass is still there — its
    /// image save protects it — so "nothing sampled" is one sampler, not none.
    /// </summary>
    private static void AssertOnlyCoresBasePassSamples(
        Ltx2WorkflowFixture fixture,
        WorkflowBridge bridge) =>
        Assert.Same(
            fixture.BaseSampler(bridge),
            Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>()));

    /// <summary>
    /// The footage encode a sourced stage samples. A stage that also carries an image guide reaches
    /// it through the in-place node's latent input rather than directly.
    /// </summary>
    private static VAEEncodeNode FootageEncodeOf(SwarmKSamplerNode sampler)
    {
        ComfyNode videoLatent = JointLatentOf(sampler).VideoLatent.Connection?.Node;
        if (videoLatent is LTXVImgToVideoInplaceNode guided)
        {
            videoLatent = guided.LatentInput.Connection?.Node;
        }
        return Assert.IsType<VAEEncodeNode>(videoLatent);
    }

    // ---- the conform chain --------------------------------------------------------------

    /// <summary>
    /// A sourced clip replaces generation with its conform chain: its stage encodes the windowed
    /// footage rather than an empty latent, samples from the step its control implies, and its
    /// output joins the generated clip at the cross-clip batch.
    /// </summary>
    [Fact]
    public async Task A_sourced_clip_refines_its_conformed_footage_and_joins_the_timeline()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(GeneratedClip(fixture), SourcedClip(SourcedStage(fixture))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertConformChain(bridge, SampledFrames);
        SwarmKSamplerNode sourced = StageSampler(bridge, 1);
        // Control 0.5 over 10 steps.
        Assert.Equal(5, sourced.StartAtStep.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(sourced), window.Id));

        BatchImagesNodeNode batch = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Same(batch, live.FinalVideoSave().Images.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, batch, window.Id));

        live.AssertAllLive(window, sourced, StageSampler(bridge, 0), batch);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The resample target is the timeline's rate, not <c>SwarmVideoResampleFPS</c>'s own 24 fps
    /// default — which is all the fixture-rate conform assertions elsewhere can read back. At 30 fps
    /// the same 0.6s slice starts 30 frames in and is 18 raw frames, snapped up to LTX's 8k+1 grid.
    /// </summary>
    [Fact]
    public async Task A_sourced_clip_conforms_to_a_non_default_timeline_rate()
    {
        const int Rate = 30;
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(SourcedClip(SourcedStage(fixture))),
            post => post["videofps"] = Rate);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(
            bridge,
            (int)(StartSeconds * Rate),
            25,
            VideoStagesWorkflowFixture.Width,
            VideoStagesWorkflowFixture.Height,
            Rate);
        SwarmKSamplerNode sourced = StageSampler(bridge, 0);
        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(sourced), window.Id));
        Assert.Equal(Rate, live.FinalVideoSave().Fps.LiteralAsDouble());

        live.AssertAllLive(window, sourced);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Without a clip duration there is no usable slice of the upload, so the footage is dropped at
    /// parse time — no loader is built at all — and the clip generates from an empty latent at the
    /// request's own frame count.
    /// </summary>
    [Fact]
    public async Task A_sourced_clip_without_a_duration_drops_the_footage_at_parse_time()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip.Remove("duration");

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());

        // Positive control: the clip still generates like any other, from an empty latent at the
        // request's frame count, guided by the host base image.
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(VideoStagesWorkflowFixture.RequestedFrames, latent.Length.LiteralAsInt());
        Assert.Same(
            latent.LATENT,
            Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(sampler).VideoLatent.Connection?.Node).LatentInput.Connection);
        // Once: the footage is dropped at parse time, so nothing downstream re-reports it.
        Assert.Single(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("source video but no usable duration"));

        live.AssertAllLive(latent, sampler);
        AssertShippable(bridge, workflow, live);
    }

    // ---- stage chains over the footage ---------------------------------------------------

    /// <summary>
    /// A second stage refines what stage 0 made of the footage: the footage encode feeds stage 0
    /// only, and stage 1 continues from stage 0's split.
    /// </summary>
    [Fact]
    public async Task A_second_stage_refines_what_stage_zero_made_of_the_footage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(SourcedClip(
            SourcedStage(fixture),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertConformChain(bridge, SampledFrames);
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        // The two stages plus core's base image pass.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Equal(5, first.StartAtStep.LiteralAsInt());
        Assert.Equal(6, second.StartAtStep.LiteralAsInt());

        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(first), window.Id));
        // Stage 1 refines stage 0's latent, guided by a decode of that same latent — so the footage
        // encode is stage 0's alone and never re-enters the chain.
        LTXVImgToVideoInplaceNode secondGuide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(second).VideoLatent.Connection?.Node);
        LTXVSeparateAVLatentNode handoff = OutputOf(bridge, first);
        Assert.Same(handoff.VideoLatent, secondGuide.LatentInput.Connection);
        Assert.Single(bridge.Graph.NodesOfType<VAEEncodeNode>());

        live.AssertAllLive(window, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A stage-0 pixel upscale re-fits the conform scale in place rather than chaining a second one:
    /// a single scale takes the raw footage straight to the stage's resolution.
    /// </summary>
    [Fact]
    public async Task Stage_zero_pixel_upscale_scales_the_footage_before_it_is_sampled()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject stage = SourcedStage(fixture);
        stage["upscale"] = 2.0;
        stage["upscaleMethod"] = "pixel-lanczos";

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(SourcedClip(stage)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertConformChain(
            bridge, SampledFrames, expectedWidth: 1024, expectedHeight: 1024);
        ImageScaleNode conform = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Same(window.Images, conform.Image.Connection);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Equal(5, sampler.StartAtStep.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(sampler), conform.Id));

        live.AssertAllLive(conform, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The same retarget when a passthrough stage 0 hands the footage to an upscaling refine stage:
    /// the refine stage's resolution is the one the conform scale is rewritten to.
    /// </summary>
    [Fact]
    public async Task A_refine_stage_pixel_upscale_retargets_the_conform_scale_in_place()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(SourcedClip(
            SourcedStage(fixture, control: 0.0),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0, steps: 12))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        ImageScaleNode conform = Assert.Single(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Same(window.Images, conform.Image.Connection);
        Assert.Equal(1024, conform.Width.LiteralAsInt());
        Assert.Equal(1024, conform.Height.LiteralAsInt());
        Assert.Equal("center", conform.Crop.LiteralAsString());

        // The passthrough stage contributes no sampler, so the refine stage is the only video one
        // alongside core's base image pass.
        SwarmKSamplerNode refine = StageSampler(bridge, 1);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        live.AssertAllLive(conform, refine);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Stage 1 has already rewritten the conform scale to its own resolution, and stage 2's
    /// <c>Stage0</c> guide runs at a higher one again. It has to stack a framing of its own on top,
    /// not re-fit the node stage 1's encode now depends on.
    /// </summary>
    [Fact]
    public async Task A_later_stage_does_not_refit_the_conform_scale_the_refine_stage_owns()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(SourcedClip(
            SourcedStage(fixture, control: 0.0),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0, steps: 12),
            fixture.Stage("Stage0", control: 0.5, upscale: 2.0, steps: 12))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        ImageScaleNode conform = ConformScaleOf(bridge, window);
        Assert.Equal(1024, conform.Width.LiteralAsInt());
        Assert.Equal(1024, conform.Height.LiteralAsInt());

        SwarmKSamplerNode refine = StageSampler(bridge, 1);
        SwarmKSamplerNode last = StageSampler(bridge, 2);
        Assert.Same(conform.IMAGE, FootageEncodeOf(refine).Pixels.Connection);
        // Stage 2 reads the conform scale and stacks its own framing on top rather than rewriting
        // it. Reachability cannot say this: stage 2 refines stage 1's latent, so the conform chain
        // is upstream of it either way — only the direct wiring separates a re-fit from a new node.
        ImageScaleNode lastFraming = Assert.IsType<ImageScaleNode>(
            Assert.IsType<LTXVPreprocessNode>(
                Assert.IsType<LTXVImgToVideoInplaceNode>(
                    JointLatentOf(last).VideoLatent.Connection?.Node).Image.Connection?.Node)
                .Image.Connection?.Node);
        Assert.Equal(2048, lastFraming.Width.LiteralAsInt());
        Assert.Same(conform.IMAGE, lastFraming.Image.Connection);

        live.AssertAllLive(conform, refine, last);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A retake window masks the encoded footage per latent frame and samples from step 0 whatever
    /// the stage's control says — the mask, not the start step, decides what regenerates.
    /// </summary>
    [Fact]
    public async Task A_retake_window_masks_the_footage_latent_and_forces_a_full_start_step()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["retake"] = new JObject
        {
            ["startSeconds"] = 0.2,
            ["lengthSeconds"] = 0.2,
            ["strength"] = 1.0,
        };

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        LTXVSetVideoLatentNoiseMasksNode mask = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        Assert.True(ReachesUpstream(bridge, mask.Samples.Connection?.Node, window.Id));

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Equal(0, sampler.StartAtStep.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, sampler.LatentImage.Connection?.Node, mask.Id));

        live.AssertAllLive(mask, sampler);
        AssertShippable(bridge, workflow, live);
    }

    // ---- IC-LoRAs over the footage --------------------------------------------------------

    /// <summary>
    /// A LipDub IC-LoRA takes its audio reference from the drive video and nothing else: the
    /// sampler's visuals stay on the footage, and the drive video's frames never reach them.
    /// </summary>
    [Fact]
    public async Task Lip_dub_takes_audio_from_the_drive_video_and_visuals_from_the_footage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture,
            IcLoraDriveData.Audio,
            Constants.IcLoraSourceUpload,
            lora: "UnitTest_LipDub",
            preset: "lipdub",
            driveMedia: new JObject
            {
                ["data"] = "data:video/mp4;base64,RFJJVkVEUklWRQ==",
                ["fileName"] = "target-voice.mp4",
            }));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadVideoB64Node drive = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>(),
            node => node.VideoBase64.LiteralAsString() == "RFJJVkVEUklWRQ==");
        GetVideoComponentsNode driveComponents = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>(),
            node => ReferenceEquals(node.Video.Connection, drive.VIDEO));
        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());

        // Audio-only drive: no visual guide is emitted at all.
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.True(
            ReachesUpstream(bridge, refTokens.AudioLatent.Connection?.Node, driveComponents.Id),
            "LipDub reference tokens do not trace to the drive video's audio.");

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(sampler), window.Id));
        // The whole video half of the joint latent, not just the footage encode: a leak would live
        // in the in-place guide branch that FootageEncodeOf unwraps past.
        Assert.False(
            ReachesUpstream(
                bridge,
                JointLatentOf(sampler).VideoLatent.Connection?.Node,
                driveComponents.Id),
            "Drive video frames leaked into the sampled visual path.");

        live.AssertAllLive(refTokens, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An Incoming audio drive on a sourced clip means the footage's own track: it is encoded once
    /// for the reference tokens, alongside the separate encode the clip conditions on.
    /// </summary>
    [Fact]
    public async Task Incoming_audio_references_the_footages_own_track()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Audio, Constants.IcLoraSourceIncoming, preset: "custom-audio"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadVideoB64Node source = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        // The stage's own generated audio latent also traces back to the upload, so reachability
        // cannot separate it from the footage's track — only the encode-of-the-trim wiring can.
        LTXVAudioVAEEncodeNode encode = Assert.IsType<LTXVAudioVAEEncodeNode>(
            refTokens.AudioLatent.Connection?.Node);
        TrimAudioDurationNode trim = Assert.IsType<TrimAudioDurationNode>(
            encode.Audio.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, trim, source.Id));

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(refTokens.Positive, sampler.Positive.Connection);

        live.AssertAllLive(refTokens, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// After a cut, Incoming audio is the previous clip's generated audio — decoded out of that
    /// clip's own output, not re-read from any upload.
    /// </summary>
    [Fact]
    public async Task Incoming_audio_after_a_cut_references_the_previous_clips_output()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = SourcedClip(SourcedStage(fixture));
        first["boundaryOut"] = Constants.BoundaryOutCut;
        JObject second = GeneratedClip(fixture);
        second["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Audio, Constants.IcLoraSourceIncoming, preset: "custom-audio"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(first, second));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstClip = StageSampler(bridge, 0);
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.True(
            ReachesUpstream(bridge, refTokens.AudioLatent.Connection?.Node, firstClip.Id),
            "Incoming audio after a cut does not trace to the previous clip's output audio.");
        Assert.Same(refTokens.Positive, StageSampler(bridge, 1).Positive.Connection);
        // The upload only ever carried video plus its own track; no separate audio was loaded.
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        live.AssertAllLive(refTokens, firstClip);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Only the opening stage encodes the footage's audio for its reference tokens; a later stage
    /// takes the audio latent its predecessor already produced, with no decode/re-encode in between.
    /// </summary>
    [Fact]
    public async Task Multi_stage_incoming_audio_reuses_the_stage_latent_without_re_encoding()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(
            SourcedStage(fixture),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Audio, Constants.IcLoraSourceIncoming, preset: "custom-audio"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXVSetAudioRefTokensNode firstTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>(),
            node => ReferenceEquals(first.Positive.Connection, node.Positive));
        LTXVSetAudioRefTokensNode secondTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>(),
            node => ReferenceEquals(second.Positive.Connection, node.Positive));

        Assert.IsType<LTXVAudioVAEEncodeNode>(firstTokens.AudioLatent.Connection?.Node);
        Assert.Same(OutputOf(bridge, first).AudioLatent, secondTokens.AudioLatent.Connection);

        live.AssertAllLive(firstTokens, secondTokens, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An Incoming visual drive on a sourced clip guides from the footage itself. Stage 0 is
    /// encode-only, so the guide must not also re-inject the footage as a pixel-space image guide.
    /// The arms are the unscoped drive and one explicitly scoped to stage 0, each checked on its
    /// own — not compared against the other.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task An_incoming_visual_drive_guides_stage_zero_from_the_conformed_footage(
        int? authoredStage)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Visual, Constants.IcLoraSourceIncoming, stage: authoredStage));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(GeneratedClip(fixture), clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        SwarmKSamplerNode sourced = StageSampler(bridge, 1);
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Same(guide.Positive, sourced.Positive.Connection);
        Assert.True(
            ReachesUpstream(bridge, guide.Image.Connection?.Node, window.Id),
            "The guide image does not trace back to the conformed footage.");
        Assert.Same(guide.Latent, JointLatentOf(sourced).VideoLatent.Connection);
        Assert.True(ReachesUpstream(bridge, guide.LatentInput.Connection?.Node, window.Id));

        // The guide frames are cropped back off after the stage samples.
        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        Assert.Same(OutputOf(bridge, sourced).VideoLatent, crop.LatentInput.Connection);
        // No footage self-reinjection: nothing frames the clip's own frames as an image guide on
        // top. The generated lead clip's host-image guide is the positive control that this pair of
        // nodes is built at all in this shape.
        Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVImgToVideoInplaceNode leadGuide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(leadGuide, JointLatentOf(StageSampler(bridge, 0)).VideoLatent.Connection?.Node);
        Assert.False(ReachesUpstream(bridge, leadGuide, window.Id));

        live.AssertAllLive(guide, crop, sourced);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An IC-LoRA scoped to a later stage emits its loader, guide and crop only there. The
    /// passthrough stage 0 has no sampler, so a loader built for it would dangle.
    /// </summary>
    [Fact]
    public async Task A_stage_scoped_ic_lora_emits_its_guide_only_on_that_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(
            SourcedStage(fixture, control: 0.0),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0, steps: 12));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Visual, Constants.IcLoraSourceIncoming, stage: 1));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(GeneratedClip(fixture), clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());

        SwarmKSamplerNode refine = StageSampler(bridge, 2);
        Assert.Same(loader.Model, refine.Model.Connection);
        Assert.Same(guide.Positive, refine.Positive.Connection);
        Assert.Same(OutputOf(bridge, refine).VideoLatent, crop.LatentInput.Connection);

        live.AssertAllLive(loader, guide, crop, refine);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A lone sourced clip retargets the request's own save. With an IC-LoRA guide extending the
    /// video latent, the audio half no longer falls out of a plain split, so the published audio has
    /// to be decoded explicitly rather than shipped as a raw latent.
    /// </summary>
    [Fact]
    public async Task A_lone_sourced_clip_with_an_ic_lora_guide_publishes_decoded_audio()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Visual, Constants.IcLoraSourceIncoming));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());

        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection?.Node, window.Id),
            "The published video does not trace back to the footage.");
        LTXVAudioVAEDecodeNode audio = Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio());
        Assert.Same(
            OutputOf(bridge, StageSampler(bridge, 0)).AudioLatent,
            audio.Samples.Connection);

        live.AssertAllLive(window, audio, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    // ---- boundaries with generated clips ---------------------------------------------------

    /// <summary>
    /// Whatever the join, a sourced lead clip publishes exactly one video: the merge. Core's base
    /// image pass is the only other sampler — a fourth would mean the host's own video root survived
    /// the timeline replacing it, and a missed save retarget would ship that root as a second video.
    /// </summary>
    [Theory]
    [InlineData("continue", true)]
    [InlineData("cut", false)]
    [InlineData("crossfade", true)]
    public async Task A_sourced_lead_clip_publishes_one_merged_save_for_every_boundary(
        string boundaryOut,
        bool blendsAtTheSeam)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject lead = SourcedClip(SourcedStage(fixture));
        lead["boundaryOut"] = boundaryOut;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(lead, GeneratedClip(fixture))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        SwarmKSamplerNode sourced = StageSampler(bridge, 0);
        SwarmKSamplerNode generated = StageSampler(bridge, 1);
        // An assertion, not a lookup: BaseSampler fails unless exactly one sampler carries core's
        // base seed. With the count below it pins the third sampler as that base pass.
        fixture.BaseSampler(bridge);
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection?.Node, window.Id),
            "The save does not trace to the cross-clip merge.");
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(save.Images.Connection!),
            generator.CurrentMedia.Path));
        Assert.Equal(
            blendsAtTheSeam,
            bridge.Graph.NodesOfType<ImageCompositeMaskedNode>().Any());

        live.AssertAllLive(window, sourced, generated, save);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A continue join freezes the tail of the sourced clip's output as the next clip's opening
    /// context, and blends the two over the same window at the seam.
    /// </summary>
    [Fact]
    public async Task A_continue_boundary_freezes_the_footages_tail_as_the_next_clips_context()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject lead = SourcedClip(SourcedStage(fixture));
        lead["boundaryOut"] = "continue";

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(lead, GeneratedClip(fixture)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        IReadOnlyList<ImageFromBatchNode> tailSlices = [.. bridge.Graph.NodesOfType<ImageFromBatchNode>()
            .Where(slice => slice.BatchIndex.LiteralAsInt() == SampledFrames - BlendFrames
                && slice.Length.LiteralAsInt() == BlendFrames
                && ReachesUpstream(bridge, slice, window.Id))];
        Assert.NotEmpty(tailSlices);

        LTXVImgToVideoInplaceNode context = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(StageSampler(bridge, 1)).VideoLatent.Connection?.Node);
        Assert.Equal(1.0, context.Strength.LiteralAsDouble());
        Assert.Contains(tailSlices, slice => ReachesUpstream(bridge, context, slice.Id));

        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(BlendFrames, ramp.Frames.LiteralAsInt());
        // The generated clip runs long by the overlap (17 + 8) so the blend does not eat into the
        // content it was authored to hold.
        Assert.Equal(
            25,
            Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>()).Length.LiteralAsInt());

        live.AssertAllLive(window, ramp, context, StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A passthrough lead clip joined by continue contributes no sampler of its own — the generated
    /// clip is the only thing sampled, and the footage still reaches the published merge.
    /// </summary>
    [Fact]
    public async Task A_passthrough_lead_joined_by_continue_samples_only_the_generated_clip()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject lead = SourcedClip(SourcedStage(fixture, control: 0.0));
        lead["boundaryOut"] = "continue";

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(lead, GeneratedClip(fixture)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        // Only the generated clip samples video; the passthrough lead contributes none, so core's
        // base image pass is the sole other sampler in the graph. BaseSampler is the assertion that
        // it is there — it fails unless exactly one sampler carries core's base seed.
        SwarmKSamplerNode generated = StageSampler(bridge, 1);
        fixture.BaseSampler(bridge);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection?.Node, window.Id),
            "The save does not trace to the merged output containing the footage.");

        live.AssertAllLive(window, generated, save);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The document's resolution, not the request's, is what a continue join runs at on both sides.
    /// Left at the request's, the overlap plan collapses and the seam repeats its frames.
    /// <para>
    /// KNOWN DEFECT (P4 in <c>nonversioned/20260804-production-findings.md</c>): this is the one
    /// shape here that cannot close with <see cref="TypedWorkflowAssertions.AssertShippable"/>. The
    /// root resizer frames the host base image to the document resolution eagerly, but a sourced
    /// clip 0 never guides from it and only a <c>continue</c> join reanchors clip 1 onto clip 0's
    /// tail — so that <c>ImageScale</c> ships reaching nothing, and <c>ImageScale</c> is not in
    /// core's <c>AutoCleanupNodeTypes</c>. <c>cut</c> and <c>crossfade</c> both hide it, because
    /// both leave clip 1's implicit <c>Generated</c> guide consuming the framing. What matching
    /// resolutions do NOT do is hide it: <c>ApplyCurrentMediaResolution</c> inserts the
    /// <c>ImageScale</c> unconditionally and <c>TryGetRootStageResolution</c> gates only on the
    /// document declaring a resolution at all, so every timeline authored with the resolution chip
    /// set is affected — only a document with no resolution escapes. The orphan set is pinned
    /// exactly below, which keeps every other orphan covered; when the resizer learns to drop an
    /// unconsumed framing, that assertion fails and this test can close like every other.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_continue_join_runs_the_generated_clip_at_the_documents_resolution()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject lead = SourcedClip(SourcedStage(fixture));
        lead["boundaryOut"] = "continue";
        JObject document = MakeDocument(lead, GeneratedClip(fixture));
        // The POST still declares 512x512; that divergence is the whole point.
        document["width"] = 768;
        document["height"] = 1024;

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertConformChain(bridge, SampledFrames, expectedWidth: 768, expectedHeight: 1024);
        EmptyLTXVLatentVideoNode generatedLatent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(768, generatedLatent.Width.LiteralAsInt());
        Assert.Equal(1024, generatedLatent.Height.LiteralAsInt());

        SwarmRampMaskBatchNode ramp = Assert.Single(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(BlendFrames, ramp.Frames.LiteralAsInt());
        Assert.Equal(768, ramp.Width.LiteralAsInt());
        Assert.Equal(1024, ramp.Height.LiteralAsInt());
        Assert.NotEmpty(bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());

        ImageScaleNode rootFraming = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => ReferenceEquals(scale.Image.Connection?.Node, BaseImage(bridge)));
        Assert.Equal([$"ImageScale#{rootFraming.Id}"], live.OrphanNodes());

        live.AssertAllLive(ramp, StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Fixed footage cannot be conditioned on the previous clip's tail, so a continue into a sourced
    /// clip degrades to a plain concat — no seam blend and no ramp mask.
    /// </summary>
    [Fact]
    public async Task A_continue_into_a_sourced_clip_degrades_to_a_cut()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject generated = GeneratedClip(fixture);
        generated["boundaryOut"] = "continue";

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(generated, SourcedClip(SourcedStage(fixture))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());

        // Positive control: the two clips still meet, as a plain batch of both decodes.
        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        BatchImagesNodeNode batch = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, batch, window.Id));
        Assert.True(ReachesUpstream(bridge, batch, StageSampler(bridge, 0).Id));

        live.AssertAllLive(window, batch);
        AssertShippable(bridge, workflow, live);
    }

    // ---- clips that publish footage without sampling it -------------------------------------

    /// <summary>
    /// Footage with nothing to sample it is the output: the conformed frames and their trimmed audio
    /// are published directly. Both authoring shapes that mean this — no stages at all, and a
    /// single Control-0 stage — reach it; the arms are asserted separately, not against each other,
    /// so this says nothing about the two graphs being identical.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sourced_clip_with_no_sampling_publishes_the_conformed_footage(
        bool withPassthroughStage)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(UnsampledSourcedClip(fixture, withPassthroughStage))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertConformChain(bridge, PassthroughFrames);
        AssertOnlyCoresBasePassSamples(fixture, bridge);

        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.Same(ConformScaleOf(bridge, window), save.Images.Connection?.Node);
        Assert.Same(
            bridge.ResolvePath((JArray)generator.CurrentMedia.Path).Node,
            save.Images.Connection?.Node);
        Assert.Equal(PassthroughFrames, generator.CurrentMedia.Frames);
        Assert.IsType<TrimAudioDurationNode>(live.PublishedAudio());

        live.AssertAllLive(window, save);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Frame trims wrap that published footage exactly once, video and audio alike, and the audio
    /// trim is expressed in seconds off the same frame numbers.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Frame_trims_wrap_the_published_footage_and_its_audio_once(
        bool withPassthroughStage)
    {
        const int trimStart = 2;
        const int trimEnd = 3;
        const int trimmedFrames = PassthroughFrames - trimStart - trimEnd;
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(UnsampledSourcedClip(fixture, withPassthroughStage)),
                    post =>
                    {
                        post["trimvideostartframes"] = trimStart;
                        post["trimvideoendframes"] = trimEnd;
                    }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertConformChain(bridge, PassthroughFrames);
        AssertOnlyCoresBasePassSamples(fixture, bridge);

        SwarmTrimFramesNode videoTrim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(trimStart, videoTrim.TrimStart.LiteralAsInt());
        Assert.Equal(trimEnd, videoTrim.TrimEnd.LiteralAsInt());
        Assert.Same(ConformScaleOf(bridge, window).IMAGE, videoTrim.Image.Connection);
        Assert.True(JToken.DeepEquals(videoTrim.IMAGE.ToPath(), generator.CurrentMedia.Path));
        Assert.Equal(trimmedFrames, generator.CurrentMedia.Frames);

        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Equal(trimStart / Fps, audioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(trimmedFrames / Fps, audioTrim.Duration.LiteralAsDouble()!.Value, precision: 6);

        live.AssertAllLive(window, videoTrim, audioTrim);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Two sourced-only clips assemble first and trim once at the end, so the trim spans the joined
    /// timeline rather than each clip.
    /// </summary>
    [Fact]
    public async Task Two_sourced_only_clips_assemble_then_trim_video_and_audio_once()
    {
        const int trimStart = 1;
        const int trimEnd = 2;
        const int trimmedFrames = (PassthroughFrames * 2) - trimStart - trimEnd;
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(SourcedClip(), SourcedClip()),
                    post =>
                    {
                        post["trimvideostartframes"] = trimStart;
                        post["trimvideoendframes"] = trimEnd;
                    }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertOnlyCoresBasePassSamples(fixture, bridge);
        // One upload, windowed twice — the clips share the decode.
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmFrameWindowNode>().Count);

        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode videoTrim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Same(merge.IMAGE, videoTrim.Image.Connection);
        Assert.True(JToken.DeepEquals(videoTrim.IMAGE.ToPath(), generator.CurrentMedia.Path));
        Assert.Equal(trimmedFrames, generator.CurrentMedia.Frames);

        AudioConcatNode audioConcat = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());
        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Same(audioConcat.AUDIO, audioTrim.Audio.Connection);
        Assert.Equal(trimStart / Fps, audioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(trimmedFrames / Fps, audioTrim.Duration.LiteralAsDouble()!.Value, precision: 6);

        live.AssertAllLive(merge, videoTrim, audioTrim);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Interpolation runs once over the published footage and doubles both the frame count and the
    /// fps, while the audio it was published with is carried through untouched.
    /// </summary>
    [Fact]
    public async Task A_sourced_only_clip_interpolates_once_and_keeps_its_audio()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(SourcedClip()), post =>
                {
                    post["videoframeinterpolationmethod"] = "RIFE";
                    post["videoframeinterpolationmultiplier"] = 2;
                }),
                extraFeatures: ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertConformChain(bridge, PassthroughFrames);
        ComfyNode rife = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "RIFE VFI");
        Assert.Same(ConformScaleOf(bridge, window).IMAGE, rife.FindInput("frames").Connection);
        Assert.Equal(2, rife.FindInput("multiplier").LiteralAsInt());

        Assert.Equal((PassthroughFrames * 2) - 1, generator.CurrentMedia.Frames);
        Assert.Equal(VideoStagesWorkflowFixture.Fps * 2, generator.CurrentMedia.GetRawFPS());
        Assert.True(JToken.DeepEquals(new JArray(rife.Id, 0), generator.CurrentMedia.Path));
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);

        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.Same(rife, save.Images.Connection?.Node);
        Assert.Equal(Fps * 2, save.Fps.LiteralAsDouble());
        Assert.True(JToken.DeepEquals(
            generator.CurrentMedia.AttachedAudio.Path,
            WorkflowBridge.ToPath(save.Audio.Connection!)));
        // Interpolation is a video-only wrapper: the audio is still the footage's own trim.
        Assert.IsType<TrimAudioDurationNode>(live.PublishedAudio());

        AssertOnlyCoresBasePassSamples(fixture, bridge);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Timeline audio segments are executed even when the clip never samples: the overlay is padded
    /// to its start offset and added to the footage's own track.
    /// </summary>
    [Fact]
    public async Task A_sourced_only_clip_executes_timeline_audio_segments_without_a_sampler()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(SourcedClip());
        document["audioTracks"] = new JArray(
            AudioTrack("track-overlay", 1.0, "overlay.wav", AudioSpan(0.1, 0.2, 0.0)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertOnlyCoresBasePassSamples(fixture, bridge);
        SwarmLoadAudioB64Node overlay = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        AudioMergeNode merge = Assert.Single(bridge.Graph.NodesOfType<AudioMergeNode>());
        Assert.Equal("add", merge.MergeMethod.LiteralAsString());
        Assert.True(ReachesUpstream(bridge, merge, overlay.Id));
        Assert.Same(merge, live.PublishedAudio());
        Assert.True(JToken.DeepEquals(
            new JArray(merge.Id, 0),
            generator.CurrentMedia.AttachedAudio.Path));

        live.AssertAllLive(merge);
        AssertShippable(bridge, workflow, live);
    }

    // ---- text-to-video ----------------------------------------------------------------------

    /// <summary>
    /// The text-to-video root replacement must not hijack a sourced clip's first stage: that path
    /// built an empty latent and orphaned the whole conform chain, so the clip rendered noise
    /// instead of the upload.
    /// </summary>
    [Fact]
    public async Task Text_to_video_clip_zero_encodes_the_footage_instead_of_an_empty_latent()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(SourcedClip(SourcedStage(fixture))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertConformChain(bridge, SampledFrames);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(5, sampler.StartAtStep.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(sampler), window.Id));

        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, window.Id));
        // Reachability would be satisfied by any audio the stage derived from the footage; the
        // claim is specifically that the stage's own sampled audio half is what ships.
        Assert.Same(
            OutputOf(bridge, sampler).AudioLatent,
            Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio()).Samples.Connection);

        live.AssertAllLive(window, sampler, save);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The payload from the original dangling-root report: a continue boundary with a widened
    /// overlap, per-stage LoRAs, an all-stages IC-LoRA drive and a pixel refine. Nothing of the
    /// text-to-video root the timeline replaced may survive — <c>SwarmKSampler</c> is not in core's
    /// unused-classes allowlist, so a stranded root sampler would ship.
    /// </summary>
    [Fact]
    public async Task Text_to_video_prunes_the_root_generation_a_sourced_clip_replaces()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_StageLora.safetensors");
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["boundaryOut"] = "continue";
        clip["boundaryOutOverlap"] = 40;
        JArray stageLoras = new(new JObject { ["name"] = "UnitTest_StageLora", ["weight"] = 1.0 });
        ((JArray)clip["stages"])[0]["loras"] = stageLoras;
        JObject refine = fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0, steps: 12);
        refine["loras"] = stageLoras.DeepClone();
        ((JArray)clip["stages"]).Add(refine);
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Visual, Constants.IcLoraSourceIncoming, lora: "UnitTest_StageLora"));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Equal(5, first.StartAtStep.LiteralAsInt());
        Assert.Equal(6, second.StartAtStep.LiteralAsInt());
        Assert.All(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => Assert.True(
                ReachesUpstream(bridge, sampler, window.Id),
                "A surviving sampler does not trace back to the clip's footage."));

        // The all-stages drive emits a loader, guide and crop on each sampling stage.
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>().Count);
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count);
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);
        // Each crop on its own stage's latent: two crops stacked on stage 0 would count the same.
        Assert.All(
            new[] { first, second },
            stage => Assert.Single(
                bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
                crop => ReferenceEquals(
                    crop.LatentInput.Connection?.Node, OutputOf(bridge, stage))));
        Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio());

        // The root save is taken over, not duplicated: the one publication AssertShippable counts
        // traces back to the footage the timeline replaced the root generation with.
        Assert.True(
            ReachesUpstream(bridge, live.FinalVideoSave().Images.Connection?.Node, window.Id),
            "The published save does not trace back to the clip's footage chain.");

        live.AssertAllLive(window, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The same pruning with no footage involved: a generated clip replacing the text-to-video root
    /// must own the only sampler and the only output.
    /// </summary>
    [Fact]
    public async Task A_generated_clip_replacing_the_text_to_video_root_leaves_no_second_sampler()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(GeneratedClip(fixture))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(save.Images.Connection!),
            generator.CurrentMedia.Path));
        Assert.Same(
            OutputOf(bridge, sampler).AudioLatent,
            Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio()).Samples.Connection);

        live.AssertAllLive(sampler, save);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A sourced lead plus a generated clip in text-to-video: the generated clip brings its own
    /// empty latent and its own audio, so the root generation has no consumer left. Its audio latent
    /// used to be wired in as the generated clip's audio init, pinning the whole root sampler alive.
    /// </summary>
    [Fact]
    public async Task A_sourced_lead_and_a_generated_clip_in_text_to_video_drop_the_root_generation()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject lead = SourcedClip(SourcedStage(fixture));
        lead["boundaryOut"] = "continue";

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(lead, GeneratedClip(fixture)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        SwarmKSamplerNode sourced = StageSampler(bridge, 0);
        SwarmKSamplerNode generated = StageSampler(bridge, 1);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection?.Node, window.Id),
            "The save does not trace to the cross-clip merge.");

        live.AssertAllLive(window, sourced, generated, save);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// <c>donotsave</c> strips every output node, which is the one state
    /// <see cref="WorkflowLivePath.AssertNoOrphanNodes"/> and <c>FinalVideoSave</c> cannot describe —
    /// with nothing to reach, "orphaned" has no meaning. Pinned as today's behaviour, not as intent:
    /// ComfyUI rejects an output-less prompt with <c>prompt_no_outputs</c>, so this request fails at
    /// the backend (P5 in <c>nonversioned/20260804-production-findings.md</c>). What the graph must
    /// still get right is that the discarded root left nothing behind.
    /// </summary>
    [Fact]
    public async Task A_do_not_save_request_publishes_nothing_and_still_prunes_the_root()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(GeneratedClip(fixture)),
                    post => post["donotsave"] = true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        live.AssertNoPublishedOutput();
        StageSampler(bridge, 0);
        Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        // The clip's decode chain goes with the saves, so the request's media path is left pointing
        // at a node that no longer exists. Nothing reads it after publication.
        Assert.Null(bridge.ResolvePath((JArray)generator.CurrentMedia.Path));

        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Every IC-LoRA guide on a generated text-to-video clip is paired with a crop after its
    /// sampler. A guide reaching a sampler that never gets one leaves the guide frames in the output.
    /// </summary>
    [Fact]
    public async Task Every_ic_lora_guide_on_a_generated_text_to_video_clip_is_cropped_back_off()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = GeneratedClip(fixture);
        clip["icLoras"] = new JArray(IcLora(
            fixture,
            IcLoraDriveData.Visual,
            Constants.IcLoraSourceUpload,
            driveMedia: new JObject
            {
                ["data"] = "data:video/mp4;base64,QUJDQUJDQUJD",
                ["fileName"] = "drive.mp4",
            }));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        LTXAddVideoICLoRAGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        Assert.Same(guide.Positive, sampler.Positive.Connection);
        Assert.Same(OutputOf(bridge, sampler).VideoLatent, crop.LatentInput.Connection);
        Assert.Same(
            crop.Latent,
            Assert.IsType<VAEDecodeTiledNode>(live.FinalVideoSave().Images.Connection?.Node)
                .Samples.Connection);

        live.AssertAllLive(guide, crop, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The host base image a generated lead clip guides from must not also be injected into a
    /// sourced clip's stage, which would in-place merge it over the encoded footage. The lead clip
    /// is the positive control: it does take the image, and it is the only thing that does.
    /// </summary>
    [Fact]
    public async Task A_sourced_clip_does_not_reinject_the_host_base_image()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(GeneratedClip(fixture), SourcedClip(SourcedStage(fixture))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        SwarmKSamplerNode generated = StageSampler(bridge, 0);
        SwarmKSamplerNode sourced = StageSampler(bridge, 1);

        // The lead clip's implicit guide, and the graph's only one.
        LTXVPreprocessNode preprocess = Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(preprocess.OutputImage, guide.Image.Connection);
        Assert.True(ReachesUpstream(bridge, preprocess, BaseImage(bridge).Id));
        Assert.Same(guide, JointLatentOf(generated).VideoLatent.Connection?.Node);

        // The sourced clip samples its footage encode and nothing of the host image.
        VAEEncodeNode footage = FootageEncodeOf(sourced);
        Assert.True(ReachesUpstream(bridge, footage, window.Id));
        Assert.False(
            ReachesUpstream(bridge, footage, BaseImage(bridge).Id),
            "The host base image was injected into the sourced clip's latent.");

        live.AssertAllLive(preprocess, guide, generated, sourced);
        AssertShippable(bridge, workflow, live);
    }
}
