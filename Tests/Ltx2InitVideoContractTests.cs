using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2InitVideoContractTests
{
    private const double ClipDuration = 0.6;

    private const double StartSeconds = 1.0;

    private const double Fps = VideoStagesWorkflowFixture.Fps;

    private const int SampledFrames = 17;

    private const int PassthroughFrames = 16;

    private const int StartFrame = (int)(StartSeconds * VideoStagesWorkflowFixture.Fps);

    private const int BlendFrames = 9;

    private static JObject SourcedClip(params JObject[] stages) =>
        SourceClip(ClipDuration, StartSeconds, stages);

    private static JObject SourcedStage(Ltx2WorkflowFixture fixture, double control = 0.5)
    {
        JObject stage = fixture.Stage(control: control, steps: 10);
        stage.Remove("imageReference");
        return stage;
    }

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

    private static ImageScaleNode ConformScaleOf(WorkflowBridge bridge, SwarmFrameWindowNode window) =>
        Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => ReferenceEquals(scale.Image.Connection?.Node, window));

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

    private static void AssertOnlyCoresBasePassSamples(
        Ltx2WorkflowFixture fixture,
        WorkflowBridge bridge) =>
        Assert.Same(
            fixture.BaseSampler(bridge),
            Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>()));

    private static VAEEncodeNode FootageEncodeOf(SwarmKSamplerNode sampler)
    {
        ComfyNode videoLatent = JointLatentOf(sampler).VideoLatent.Connection?.Node;
        if (videoLatent is LTXVImgToVideoInplaceNode guided)
        {
            videoLatent = guided.LatentInput.Connection?.Node;
        }
        return Assert.IsType<VAEEncodeNode>(videoLatent);
    }


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
        Assert.Equal(5, sourced.StartAtStep.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(sourced), window.Id));

        BatchImagesNodeNode batch = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Same(batch, live.FinalVideoSave().Images.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, batch, window.Id));

        live.AssertAllLive(window, sourced, StageSampler(bridge, 0), batch);
        AssertShippable(bridge, workflow, live);
    }

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

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(VideoStagesWorkflowFixture.RequestedFrames, latent.Length.LiteralAsInt());
        Assert.Same(
            latent.LATENT,
            Assert.IsType<LTXVImgToVideoInplaceNode>(
                JointLatentOf(sampler).VideoLatent.Connection?.Node).LatentInput.Connection);
        Assert.Single(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("source video but no usable duration"));

        live.AssertAllLive(latent, sampler);
        AssertShippable(bridge, workflow, live);
    }


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
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Equal(5, first.StartAtStep.LiteralAsInt());
        Assert.Equal(6, second.StartAtStep.LiteralAsInt());

        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(first), window.Id));
        // Only stage 0 encodes the footage.
        LTXVImgToVideoInplaceNode secondGuide = Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(second).VideoLatent.Connection?.Node);
        LTXVSeparateAVLatentNode handoff = OutputOf(bridge, first);
        Assert.Same(handoff.VideoLatent, secondGuide.LatentInput.Connection);
        Assert.Single(bridge.Graph.NodesOfType<VAEEncodeNode>());

        live.AssertAllLive(window, first, second);
        AssertShippable(bridge, workflow, live);
    }

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

        SwarmKSamplerNode refine = StageSampler(bridge, 1);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        live.AssertAllLive(conform, refine);
        AssertShippable(bridge, workflow, live);
    }

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
        // Direct wiring distinguishes added framing from a rewritten conform scale.
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


    [Fact]
    public async Task Lip_dub_takes_audio_from_the_drive_video_and_visuals_from_the_footage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture,
            IcLoraDriveData.Audio,
            MediaSource.Upload,
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

        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.True(
            ReachesUpstream(bridge, refTokens.AudioLatent.Connection?.Node, driveComponents.Id),
            "LipDub reference tokens do not trace to the drive video's audio.");

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.True(ReachesUpstream(bridge, FootageEncodeOf(sampler), window.Id));
        // Check the whole video latent because guides wrap its footage encode.
        Assert.False(
            ReachesUpstream(
                bridge,
                JointLatentOf(sampler).VideoLatent.Connection?.Node,
                driveComponents.Id),
            "Drive video frames leaked into the sampled visual path.");

        live.AssertAllLive(refTokens, sampler);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Incoming_audio_references_the_footages_own_track()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Audio, MediaSource.Incoming, preset: "custom-audio"));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadVideoB64Node source = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        // Direct wiring distinguishes source audio from generated audio.
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

    [Fact]
    public async Task Incoming_audio_after_a_cut_references_the_previous_clips_output()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject first = SourcedClip(SourcedStage(fixture));
        first["boundaryOut"] = Constants.BoundaryOutCut;
        JObject second = GeneratedClip(fixture);
        second["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Audio, MediaSource.Incoming, preset: "custom-audio"));

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
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        live.AssertAllLive(refTokens, firstClip);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Multi_stage_incoming_audio_reuses_the_stage_latent_without_re_encoding()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(
            SourcedStage(fixture),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Audio, MediaSource.Incoming, preset: "custom-audio"));

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

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task An_incoming_visual_drive_guides_stage_zero_from_the_conformed_footage(
        int? authoredStage)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Visual, MediaSource.Incoming, stage: authoredStage));

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

        LTXVCropGuidesNode crop = Assert.Single(bridge.Graph.NodesOfType<LTXVCropGuidesNode>());
        Assert.Same(OutputOf(bridge, sourced).VideoLatent, crop.LatentInput.Connection);
        Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVImgToVideoInplaceNode leadGuide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(leadGuide, JointLatentOf(StageSampler(bridge, 0)).VideoLatent.Connection?.Node);
        Assert.False(ReachesUpstream(bridge, leadGuide, window.Id));

        live.AssertAllLive(guide, crop, sourced);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_stage_scoped_ic_lora_emits_its_guide_only_on_that_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(
            SourcedStage(fixture, control: 0.0),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 2.0, steps: 12));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Visual, MediaSource.Incoming, stage: 1));

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

    [Fact]
    public async Task A_lone_sourced_clip_with_an_ic_lora_guide_publishes_decoded_audio()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourcedClip(SourcedStage(fixture));
        clip["icLoras"] = new JArray(IcLora(
            fixture, IcLoraDriveData.Visual, MediaSource.Incoming));

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
        // The generated side includes the eight overlap frames.
        Assert.Equal(
            25,
            Assert.Single(bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>()).Length.LiteralAsInt());

        live.AssertAllLive(window, ramp, context, StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

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

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => ReferenceEquals(scale.Image.Connection?.Node, BaseImage(bridge)));

        live.AssertAllLive(ramp, StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

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

        SwarmFrameWindowNode window = Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        BatchImagesNodeNode batch = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, batch, window.Id));
        Assert.True(ReachesUpstream(bridge, batch, StageSampler(bridge, 0).Id));

        live.AssertAllLive(window, batch);
        AssertShippable(bridge, workflow, live);
    }


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
        // Both clips reuse the same upload window.
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());

        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Equal(2, merge.Images.Items.Count);
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
        Assert.IsType<TrimAudioDurationNode>(live.PublishedAudio());

        AssertOnlyCoresBasePassSamples(fixture, bridge);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Trim runs before interpolation. The request's trim counts are source frames, so a trim
    /// applied after RIFE would cut interpolated frames instead, and would read the doubled frame
    /// rate when it converted its own trim into an audio offset.
    /// </summary>
    [Fact]
    public async Task A_sourced_only_clip_trims_source_frames_before_it_interpolates()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        const int trimStart = 2;
        const int trimEnd = 3;
        const int trimmedFrames = PassthroughFrames - trimStart - trimEnd;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(SourcedClip()), post =>
                {
                    post["trimvideostartframes"] = trimStart;
                    post["trimvideoendframes"] = trimEnd;
                    post["videoframeinterpolationmethod"] = "RIFE";
                    post["videoframeinterpolationmultiplier"] = 2;
                }),
                extraFeatures: ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmTrimFramesNode videoTrim =
            Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        ComfyNode rife = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "RIFE VFI");
        Assert.Same(videoTrim.IMAGE, rife.FindInput("frames").Connection);
        Assert.Equal((trimmedFrames * 2) - 1, generator.CurrentMedia.Frames);

        // The audio offset uses the source frame rate, not the interpolated one.
        TrimAudioDurationNode audioTrim =
            Assert.IsType<TrimAudioDurationNode>(live.PublishedAudio());
        Assert.Equal(trimStart / Fps, audioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(trimmedFrames / Fps, audioTrim.Duration.LiteralAsDouble()!.Value, precision: 6);

        live.AssertAllLive(videoTrim, audioTrim);
        AssertShippable(bridge, workflow, live);
    }

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
        // Direct wiring proves the sampled audio half is published.
        Assert.Same(
            OutputOf(bridge, sampler).AudioLatent,
            Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio()).Samples.Connection);

        live.AssertAllLive(window, sampler, save);
        AssertShippable(bridge, workflow, live);
    }

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
            fixture, IcLoraDriveData.Visual, MediaSource.Incoming, lora: "UnitTest_StageLora"));

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

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count);
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);
        // Count alone would not prove one crop belongs to each stage.
        Assert.All(
            new[] { first, second },
            stage => Assert.Single(
                bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
                crop => ReferenceEquals(
                    crop.LatentInput.Connection?.Node, OutputOf(bridge, stage))));
        Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio());

        Assert.True(
            ReachesUpstream(bridge, live.FinalVideoSave().Images.Connection?.Node, window.Id),
            "The published save does not trace back to the clip's footage chain.");

        live.AssertAllLive(window, first, second);
        AssertShippable(bridge, workflow, live);
    }

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

    [Fact]
    public async Task Every_ic_lora_guide_on_a_generated_text_to_video_clip_is_cropped_back_off()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = GeneratedClip(fixture);
        clip["icLoras"] = new JArray(IcLora(
            fixture,
            IcLoraDriveData.Visual,
            MediaSource.Upload,
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

        LTXVPreprocessNode preprocess = Assert.Single(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        LTXVImgToVideoInplaceNode guide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.Same(preprocess.OutputImage, guide.Image.Connection);
        Assert.True(ReachesUpstream(bridge, preprocess, BaseImage(bridge).Id));
        Assert.Same(guide, JointLatentOf(generated).VideoLatent.Connection?.Node);

        VAEEncodeNode footage = FootageEncodeOf(sourced);
        Assert.True(ReachesUpstream(bridge, footage, window.Id));
        Assert.False(
            ReachesUpstream(bridge, footage, BaseImage(bridge).Id),
            "The host base image was injected into the sourced clip's latent.");

        live.AssertAllLive(preprocess, guide, generated, sourced);
        AssertShippable(bridge, workflow, live);
    }
}
