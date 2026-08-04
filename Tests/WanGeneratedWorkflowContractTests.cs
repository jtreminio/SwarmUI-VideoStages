using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// How a VideoStages timeline drives WAN, generated through the real Comfy API POST path.
/// <para>
/// The POST shape decides the topology. Image-to-video keeps core's base pass (sampler seed
/// <c>Seed</c>, published through its own <c>SaveImage</c>) and hands the decoded base image to the
/// timeline, while the extension prunes core's own WAN video root — so the stage sampler is the
/// only video sampler left even though core's image-to-video sampler would have shared stage 0's
/// seed. Text-to-video has no base pass at all; core's empty latent is either reused by stage 0 or
/// swept when the stage builds its own.
/// </para>
/// <para>
/// The WAN checkpoints live under <c>diffusion_models</c>, so <c>T2IModel.IsDiffusionModelsFormat</c>
/// routes them to a UNET-only loader — the video half of these graphs never has a
/// <c>CheckpointLoaderSimple</c>, and the one that exists in the image-to-video shape belongs to
/// core's SDXL base model.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class WanGeneratedWorkflowContractTests
{
    /// <summary>A 1x1 PNG: <c>ValidateParam</c> rejects IMAGE payloads under 10 base64 characters,
    /// and an undecodable one is rejected later still.</summary>
    private const string EndImagePayload =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42m"
        + "NkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    /// <summary>Frames a 0.6s source clip conforms to on WAN's 4k+1 grid at 24 fps.</summary>
    private const int SourceClipFrames = 17;

    /// <summary>The source window opens one second in.</summary>
    private const int SourceClipStartFrame = 24;

    /// <summary>
    /// A clip that refines uploaded footage instead of generating from noise. Stage 0's default
    /// <c>Generated</c> reference is stripped so the source, not a root donor, is its input.
    /// </summary>
    private static JObject SourceClip(params JObject[] stages)
    {
        stages[0].Remove("imageReference");
        JObject clip = MakeClip(0.6, stages);
        JObject source = SourceVideo();
        source["startSeconds"] = 1.0;
        clip["initVideo"] = source;
        return clip;
    }

    /// <summary>
    /// The conform chain a source clip is refined from: load, split, resample to the timeline fps,
    /// window to the clip's span, then scale to the timeline resolution. Returns the frame window,
    /// which is what everything downstream measures its length against.
    /// </summary>
    private static SwarmFrameWindowNode AssertSourceConformChain(WorkflowBridge bridge)
    {
        SwarmLoadVideoB64Node load = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(load, components.Video.Connection?.Node);
        SwarmVideoResampleFPSNode resample = Assert.Single(
            bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.Same(components, resample.ImagesInput.Connection?.Node);
        Assert.Same(components, resample.FpsIn.Connection?.Node);
        Assert.Equal(24.0, resample.FpsOut.LiteralAsDouble());
        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Same(resample, window.ImagesInput.Connection?.Node);
        Assert.Equal(SourceClipStartFrame, window.StartFrame.LiteralAsInt());
        Assert.Equal(SourceClipFrames, window.FrameCount.LiteralAsInt());
        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node == window);
        Assert.Equal(512, scale.Width.LiteralAsInt());
        Assert.Equal(512, scale.Height.LiteralAsInt());
        return window;
    }

    /// <summary>The single-frame donor WAN conditions from, and the framing scale behind it.</summary>
    private static ImageScaleNode FirstFrameFraming(ComfyNode startImage)
    {
        ImageFromBatchNode donor = Assert.IsType<ImageFromBatchNode>(startImage);
        Assert.Equal(0, donor.BatchIndex.LiteralAsInt());
        Assert.Equal(1, donor.Length.LiteralAsInt());
        return Assert.IsType<ImageScaleNode>(donor.Image.Connection?.Node);
    }

    private static JObject StageWithLoras(JObject stage, params JObject[] loras)
    {
        stage["loras"] = new JArray(loras);
        return stage;
    }

    private static JObject Lora(string name, double weight, double? textEncoderWeight = null)
    {
        JObject lora = new()
        {
            ["name"] = name,
            ["weight"] = weight,
        };
        if (textEncoderWeight is not null)
        {
            lora["textEncoderWeight"] = textEncoderWeight.Value;
        }
        return lora;
    }

    // ---- host handoff -------------------------------------------------------------------

    /// <summary>
    /// A WAN clip takes over core's video root: core's base image pass survives and feeds the
    /// timeline, but core's own WAN chain does not. Two samplers is the whole claim — core's base
    /// pass and one stage — so no hidden second video pass runs alongside. Wan 2.1 additionally
    /// carries a CLIP-vision encode of the same donor frame; Wan 2.2 does not.
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, false)]
    [InlineData(WanWorkflowFixture.Wan21I2v14bFixturePath, true)]
    public async Task A_wan_clip_replaces_cores_video_root_and_drives_from_the_host_base_image(
        string modelFixturePath,
        bool expectsClipVision)
    {
        using WanWorkflowFixture fixture =
            WanWorkflowFixture.CreateWithBaseModel(modelFixturePath);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(fixture.Stage(control: 0.5, steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Equal(10, stage.Steps.LiteralAsInt());
        Assert.IsType<UNETLoaderNode>(stage.Model.Connection?.Node);
        Assert.IsType<CheckpointLoaderSimpleNode>(
            fixture.BaseSampler(bridge).Model.Connection?.Node);

        WanImageToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Same(conditioning.Positive, stage.Positive.Connection);
        Assert.Same(conditioning.Negative, stage.Negative.Connection);
        Assert.Same(conditioning.Latent, stage.LatentImage.Connection);
        Assert.Equal(25, conditioning.Length.LiteralAsInt());

        ImageScaleNode framing = FirstFrameFraming(conditioning.StartImage.Connection?.Node);
        Assert.Same(
            BaseImage(bridge, fixture.BaseSampler(bridge)), framing.Image.Connection?.Node);
        Assert.Equal(512, framing.Width.LiteralAsInt());
        Assert.Equal(512, framing.Height.LiteralAsInt());

        if (expectsClipVision)
        {
            CLIPVisionEncodeNode vision = Assert.IsType<CLIPVisionEncodeNode>(
                conditioning.ClipVisionOutput.Connection?.Node);
            Assert.Same(conditioning.StartImage.Connection, vision.Image.Connection);
            live.AssertLive(vision);
        }
        else
        {
            Assert.Null(conditioning.ClipVisionOutput.Connection);
            Assert.Empty(bridge.Graph.NodesOfType<CLIPVisionEncodeNode>());
        }

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        live.AssertAllLive(conditioning, framing, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The request's legacy video-swap fields never become a second video pass. The swap model is
    /// a real, separately registered checkpoint here, so "only the stage's model is loaded" is a
    /// fact about this graph and not about an unresolvable name.
    /// </summary>
    [Fact]
    public async Task Legacy_video_swap_fields_warn_and_build_no_second_video_pass()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(fixture.Stage(control: 0.5, steps: 10))),
                    post =>
                    {
                        post["videoswapmodel"] = fixture.LowNoiseModel.Name;
                        post["videoswappercent"] = 0.5;
                    }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        UNETLoaderNode loader = Assert.Single(bridge.Graph.NodesOfType<UNETLoaderNode>());
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bHighNoiseFixturePath),
            loader.UnetName.LiteralAsString());
        Assert.False(generator.IsImageToVideoSwap);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(
                "Create separate timeline stages", StringComparison.Ordinal));

        live.AssertAllLive(loader, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    // ---- native latent entry ------------------------------------------------------------

    /// <summary>
    /// Wan 2.2 TI2V 5B conditions through its own latent node rather than the 14B conditioning
    /// wrapper: the donor image goes into <c>Wan22ImageToVideoLatent</c> and the text encoders feed
    /// the sampler directly.
    /// </summary>
    [Fact]
    public async Task Wan5b_generated_entry_samples_its_native_latent_from_the_host_image()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel(
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(fixture.Stage(steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Wan22ImageToVideoLatentNode latent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Same(latent, stage.LatentImage.Connection?.Node);
        Assert.Equal(512, latent.Width.LiteralAsInt());
        Assert.Equal(512, latent.Height.LiteralAsInt());
        Assert.Equal(25, latent.Length.LiteralAsInt());
        Assert.Equal(10, stage.Steps.LiteralAsInt());
        Assert.Equal(0, stage.StartAtStep.LiteralAsInt());
        Assert.Same(
            BaseImage(bridge, fixture.BaseSampler(bridge)),
            FirstFrameFraming(latent.StartImage.Connection?.Node).Image.Connection?.Node);
        // The 5B profile never routes through the 14B conditioning node; the 14B tests are the
        // positive control that this graph shape exists at all.
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Null(generator.CurrentMedia.AttachedAudio);

        live.AssertAllLive(latent, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>Text-to-video has no image donor, so the native latent is built bare.</summary>
    [Fact]
    public async Task Wan5b_text_entry_builds_its_native_latent_without_an_image_donor()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create(
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip(fixture.Stage(steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Wan22ImageToVideoLatentNode latent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        Assert.Null(latent.StartImage.Connection);
        Assert.Equal(25, latent.Length.LiteralAsInt());
        Assert.Equal(512, latent.Width.LiteralAsInt());
        Assert.Equal(512, latent.Height.LiteralAsInt());

        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(latent, stage.LatentImage.Connection?.Node);
        Assert.Equal(10, stage.Steps.LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertAllLive(latent, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// With no requested frame count at all the architecture's own 81-frame default takes over —
    /// the 25 every other text-to-video test here produces is the control that the POST value is
    /// what normally decides it.
    /// </summary>
    [Fact]
    public async Task Wan5b_text_entry_defaults_to_eighty_one_frames_without_a_requested_count()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create(
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(MakeClip(fixture.Stage(steps: 10))),
                    post => post.Remove("text2videoframes")));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Wan22ImageToVideoLatentNode latent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        Assert.Null(latent.StartImage.Connection);
        Assert.Equal(81, latent.Length.LiteralAsInt());
        Assert.Equal(81, generator.CurrentMedia.Frames);

        live.AssertAllLive(latent, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A second stage on a text-to-video 5B clip has no host image to fall back on, so it chains
    /// from the first stage's decoded video: a second native latent, this one carrying a start
    /// image traced back through the decode to the first sampler.
    /// </summary>
    [Fact]
    public async Task Wan5b_text_entry_chains_a_later_stage_from_the_first_decoded_video()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create(
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip(
                    fixture.Stage(steps: 10),
                    fixture.Stage("PreviousStage", steps: 11)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Wan22ImageToVideoLatentNode native = Assert.IsType<Wan22ImageToVideoLatentNode>(
            first.LatentImage.Connection?.Node);
        Wan22ImageToVideoLatentNode continuation = Assert.IsType<Wan22ImageToVideoLatentNode>(
            second.LatentImage.Connection?.Node);
        Assert.Null(native.StartImage.Connection);
        Assert.True(ReachesUpstream(
            bridge,
            continuation.StartImage.Connection?.Node,
            first.Id));
        Assert.Equal(25, generator.CurrentMedia.Frames);

        live.AssertAllLive(native, continuation, first, second);
        AssertShippable(bridge, workflow, live);
    }

    // ---- authored frame references ------------------------------------------------------

    /// <summary>
    /// An uploaded first-frame reference gives a text-to-video clip a donor of its own, so it
    /// conditions through <c>WanImageToVideo</c> rather than sampling core's empty latent.
    /// </summary>
    [Fact]
    public async Task An_uploaded_first_frame_reference_conditions_a_text_root()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["refs"] = new JArray(UploadedReference("RklSU1Q="));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("RklSU1Q=", upload.ImageBase64.LiteralAsString());
        WanImageToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Same(
            upload,
            FirstFrameFraming(conditioning.StartImage.Connection?.Node).Image.Connection?.Node);

        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(conditioning.Latent, stage.LatentImage.Connection);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyHunyuanLatentVideoNode>());

        live.AssertAllLive(upload, conditioning, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An unusable first-frame reference warns and drops back to the plain empty latent rather
    /// than failing the request or leaving a half-built donor behind.
    /// </summary>
    [Theory]
    [InlineData(false, "missing inline data and a file name")]
    [InlineData(true, "Ignoring invalid WAN first-frame reference payload")]
    public async Task An_unusable_first_frame_reference_warns_and_falls_back_to_the_empty_latent(
        bool malformed,
        string expectedWarning)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create();
        JObject reference = MakeRef("Upload");
        if (malformed)
        {
            reference["uploadedImage"] = new JObject
            {
                ["data"] = "not-an-image-payload",
                ["fileName"] = "broken.png",
            };
        }
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["refs"] = new JArray(reference);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyHunyuanLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyHunyuanLatentVideoNode>());
        Assert.Equal(25, latent.Length.LiteralAsInt());
        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(latent, stage.LatentImage.Connection?.Node);
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(expectedWarning, StringComparison.Ordinal));

        live.AssertAllLive(latent, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A last-only reference routes to the first/last conditioning node with no start image at
    /// all. Wan 2.1 encodes the end frame for CLIP-vision as well; Wan 2.2 does not.
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, false)]
    [InlineData(WanWorkflowFixture.Wan21I2v14bFixturePath, true)]
    public async Task An_uploaded_last_frame_reference_conditions_a_text_root_without_a_donor(
        string modelFixturePath,
        bool expectsClipVision)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create(modelFixturePath);
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["refs"] = new JArray(UploadedReference("TEFTVA==", fromEnd: true));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        WanFirstLastFrameToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Null(conditioning.StartImage.Connection);
        Assert.Null(conditioning.ClipVisionStartImage.Connection);
        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());
        // The end frame is framed but never unwrapped to one frame — it already is one.
        ImageScaleNode framing = Assert.IsType<ImageScaleNode>(
            conditioning.EndImage.Connection?.Node);
        Assert.Same(upload, framing.Image.Connection?.Node);

        if (expectsClipVision)
        {
            CLIPVisionEncodeNode vision = Assert.IsType<CLIPVisionEncodeNode>(
                conditioning.ClipVisionEndImage.Connection?.Node);
            Assert.Same(framing.IMAGE, vision.Image.Connection);
            live.AssertLive(vision);
        }
        else
        {
            Assert.Null(conditioning.ClipVisionEndImage.Connection);
            Assert.Empty(bridge.Graph.NodesOfType<CLIPVisionEncodeNode>());
        }

        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<EmptyHunyuanLatentVideoNode>());
        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(conditioning.Latent, stage.LatentImage.Connection);

        live.AssertAllLive(upload, conditioning, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// One bad reference does not take the other down with it: the malformed first frame is warned
    /// about and dropped, and the clip still conditions on its valid last frame.
    /// </summary>
    [Fact]
    public async Task A_malformed_first_reference_degrades_to_last_only_conditioning()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["refs"] = new JArray(
            UploadedReference("not-valid-base64"),
            UploadedReference("TEFTVA==", fromEnd: true));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        WanFirstLastFrameToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Null(conditioning.StartImage.Connection);
        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());
        Assert.NotNull(conditioning.EndImage.Connection);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(
                "Ignoring invalid WAN first-frame reference payload",
                StringComparison.Ordinal));

        live.AssertAllLive(upload, conditioning, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A clip's last-frame reference belongs to the last stage that actually generates. Stage 0
    /// conditions on the host image alone, stage 1 owns the end frame, and stage 2 — authored at
    /// <c>control: 0</c> — is a passthrough with no sampler for the reference to land on.
    /// </summary>
    [Fact]
    public async Task An_uploaded_last_frame_reference_belongs_to_the_terminal_generating_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12),
            fixture.Stage("PreviousStage", control: 0, steps: 13));
        clip["refs"] = new JArray(UploadedReference("TEFTVA==", fromEnd: true));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());
        WanImageToVideoNode opening = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        WanFirstLastFrameToVideoNode terminal = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.True(ReachesUpstream(bridge, terminal.EndImage.Connection?.Node, upload.Id));

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Same(opening.Positive, first.Positive.Connection);
        Assert.Same(terminal.Positive, second.Positive.Connection);
        Assert.Equal(10, first.Steps.LiteralAsInt());
        Assert.Equal(12, second.Steps.LiteralAsInt());
        // The passthrough stage contributes no pass of its own; core's base sampler is the third.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => sampler.Steps.LiteralAsInt() == 13);

        live.AssertAllLive(upload, opening, terminal, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Clip-local references are the clip's own business: with both authored, the request's global
    /// end image is neither loaded into the graph nor consumed off the request.
    /// </summary>
    [Fact]
    public async Task Clip_local_uploads_leave_the_requests_global_end_image_untouched()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage(steps: 10));
        clip["refs"] = new JArray(
            UploadedReference("RklSU1Q="),
            UploadedReference("TEFTVA==", fromEnd: true));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(clip), post => post["videoendimage"] = EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node[] uploads = [.. bridge.Graph.NodesOfType<SwarmLoadImageB64Node>()];
        Assert.Equal(
            ["RklSU1Q=", "TEFTVA=="],
            uploads.Select(upload => upload.ImageBase64.LiteralAsString()).Order());
        WanFirstLastFrameToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.StartImage.Connection?.Node,
            Assert.Single(uploads, upload =>
                upload.ImageBase64.LiteralAsString() == "RklSU1Q=").Id));
        Assert.True(ReachesUpstream(
            bridge,
            conditioning.EndImage.Connection?.Node,
            Assert.Single(uploads, upload =>
                upload.ImageBase64.LiteralAsString() == "TEFTVA==").Id));

        // The global end image really was accepted by the request — it is simply never used, and
        // the extension leaves it on the input rather than consuming it.
        Assert.NotNull(generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null));
        Assert.Empty(bridge.Graph.NodesOfType<LoadImageNode>());

        live.AssertAllLive(conditioning, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    // ---- frame counts -------------------------------------------------------------------

    /// <summary>
    /// An authored clip duration replaces the request's frame count and is snapped onto WAN's 4k+1
    /// grid — 13 frames of footage become 17. The request's own value is left as the user set it,
    /// so a later consumer still sees 25.
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan21I2v14bFixturePath)]
    [InlineData(WanWorkflowFixture.Wan21T2v14bFixturePath)]
    public async Task Wan21_text_entry_snaps_the_clip_duration_and_restores_the_requested_count(
        string modelFixturePath)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create(modelFixturePath);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip(13.0 / 24.0, fixture.Stage(steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyHunyuanLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyHunyuanLatentVideoNode>());
        Assert.Equal(17, latent.Length.LiteralAsInt());
        Assert.Equal(17, generator.CurrentMedia.Frames);
        Assert.Equal(25, generator.UserInput.Get(T2IParamTypes.Text2VideoFrames));
        Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());

        live.AssertAllLive(latent, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    // ---- source clips -------------------------------------------------------------------

    /// <summary>
    /// A source clip refines uploaded footage: stage 0 conditions on the conformed first frame,
    /// stage 1 re-encodes stage 0's decode and starts partway through its schedule, and the
    /// trailing passthrough stage adds nothing. Every length in the chain follows the conform
    /// window, not the request.
    /// </summary>
    [Fact]
    public async Task Wan5b_source_clip_refines_the_conformed_video_and_drops_a_passthrough_tail()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel(
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(SourceClip(
                    fixture.Stage(control: 1, steps: 10),
                    fixture.Stage("PreviousStage", control: 0.5, steps: 12),
                    fixture.Stage("PreviousStage", control: 0, steps: 13)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        // The source replaces the host root entirely, so core's base pass is gone too.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        Wan22ImageToVideoLatentNode latent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        Assert.Same(latent, first.LatentImage.Connection?.Node);
        Assert.Equal(SourceClipFrames, latent.Length.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, latent.StartImage.Connection?.Node, window.Id));

        // control 0.5 over 12 steps starts halfway through the schedule.
        Assert.Equal(6, second.StartAtStep.LiteralAsInt());
        VAEEncodeNode reEncode = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, reEncode, first.Id));
        Assert.Equal(SourceClipFrames, generator.CurrentMedia.Frames);

        live.AssertAllLive(window, latent, reEncode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>A clip-level LoRA reaches the stage generated off that clip's source.</summary>
    [Fact]
    public async Task A_source_clip_lora_applies_to_its_generating_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Source_Lora.safetensors");
        JObject clip = SourceClip(fixture.Stage(control: 0.5, steps: 10));
        clip["loras"] = new JArray(Lora("UnitTest_Wan_Source_Lora", 0.6));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        AssertModelOnlyLora(
            LoraLoaderNodesOf(bridge),
            "UnitTest_Wan_Source_Lora.safetensors",
            0.6);
        LoraLoaderModelOnlyNode lora = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>());
        Assert.Same(lora, stage.Model.Connection?.Node);
        Assert.IsType<UNETLoaderNode>(lora.Model.Connection?.Node);
        Assert.Null(generator.CurrentMedia.AttachedAudio);

        live.AssertAllLive(window, lora, stage);
        AssertShippable(bridge, workflow, live);
    }

    // ---- LoRAs --------------------------------------------------------------------------

    /// <summary>
    /// WAN's compat class does not target the text encoder, so both prompt-tagged and stage
    /// LoRAs load through <c>LoraLoaderModelOnly</c> with the authored text-encoder weight dropped,
    /// and a zero model weight removes the loader entirely. Prompt LoRAs load first and the stage's
    /// own chain on top, so the sampler reads the end of one chain.
    /// <para>
    /// The confined arm additionally sets a request-level LoRA scoped to <c>BaseOnly</c>: it must
    /// not reach the video sampler, and it must still be on the request afterwards.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, false)]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, false)]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, true)]
    public async Task Prompt_and_stage_loras_load_model_only_and_compose_in_order(
        string modelFixturePath,
        bool textEntryWithConfinedHostLora)
    {
        using WanWorkflowFixture fixture = textEntryWithConfinedHostLora
            ? WanWorkflowFixture.Create(modelFixturePath)
            : WanWorkflowFixture.CreateWithBaseModel(modelFixturePath);
        foreach (string name in new[]
        {
            "UnitTest_Wan_Prompt", "UnitTest_Wan_Persisted",
            "UnitTest_Wan_PromptZero", "UnitTest_Wan_PersistedZero",
            "UnitTest_Wan_Base_Confined",
        })
        {
            fixture.InstallModel("LoRA", $"{name}.safetensors");
        }
        JObject stage = StageWithLoras(
            fixture.Stage(steps: 10),
            Lora("UnitTest_Wan_Persisted", 0.6, textEncoderWeight: 0.7),
            Lora("UnitTest_Wan_PersistedZero", 0, textEncoderWeight: 0.9));
        void Customize(JObject post)
        {
            post["prompt"] =
                "global <videoclip[0,0]><lora:UnitTest_Wan_Prompt:0.4:0.8>"
                + "<lora:UnitTest_Wan_PromptZero:0:0.9>";
            if (!textEntryWithConfinedHostLora)
            {
                return;
            }
            post["loras"] = "UnitTest_Wan_Base_Confined";
            post["loraweights"] = "0.95";
            post["loratencweights"] = "0.85";
            post["lorasectionconfinement"] = $"{T2IParamInput.SectionID_BaseOnly}";
        }
        JObject document = MakeDocument(MakeClip(stage));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                textEntryWithConfinedHostLora
                    ? fixture.Post(document, Customize)
                    : fixture.ImageToVideoPost(document, Customize));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        List<ComfyNode> loras = LoraLoaderNodesOf(bridge);
        AssertModelOnlyLora(loras, "UnitTest_Wan_Prompt.safetensors", 0.4);
        AssertModelOnlyLora(loras, "UnitTest_Wan_Persisted.safetensors", 0.6);
        Assert.Equal(2, loras.Count);

        LoraLoaderModelOnlyNode prompt = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.LoraName.LiteralAsString() == "UnitTest_Wan_Prompt.safetensors");
        LoraLoaderModelOnlyNode persisted = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.LoraName.LiteralAsString() == "UnitTest_Wan_Persisted.safetensors");
        Assert.IsType<UNETLoaderNode>(prompt.Model.Connection?.Node);
        Assert.Same(prompt, persisted.Model.Connection?.Node);
        SwarmKSamplerNode stageSampler = StageSampler(bridge, 0);
        Assert.Same(persisted, stageSampler.Model.Connection?.Node);

        // The request's own LoRA list is exactly what it was before the stages ran: the two the
        // prompt parser put there (a zero weight still counts) plus whatever the POST set. The
        // stage's LoRAs are borrowed and handed back, so they must not appear.
        Assert.Equal(
            textEntryWithConfinedHostLora
                ? ["UnitTest_Wan_Base_Confined", "UnitTest_Wan_Prompt", "UnitTest_Wan_PromptZero"]
                : new[] { "UnitTest_Wan_Prompt", "UnitTest_Wan_PromptZero" },
            generator.UserInput.Get(T2IParamTypes.Loras));
        // The borrowed host model-loader cache goes back too: StageModelLoadScope drops this key on
        // dispose whenever it applied a LoRA scope, so the loader it built under one cannot be
        // handed to a later consumer that is not under it.
        Assert.DoesNotContain(
            $"modelloader_{fixture.Model.Name}_image2video",
            generator.NodeHelpers.Keys);

        if (textEntryWithConfinedHostLora)
        {
            // Confined to the base pass, which a text-to-video request does not have — so it never
            // reaches the graph even though it is still on the request.
            Wan22ImageToVideoLatentNode latent = Assert.Single(
                bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
            Assert.Null(latent.StartImage.Connection);
            Assert.False(generator.IsImageToVideo);
            Assert.False(generator.IsImageToVideoSwap);
        }

        live.AssertAllLive(prompt, persisted, stageSampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Each stage's LoRAs are its own: two stages naming the same file at different weights get one
    /// loader each, and a third stage naming none samples straight off the shared UNET loader that
    /// both loaders also branch from — the loader is cached, the LoRA scope is not.
    /// </summary>
    [Fact]
    public async Task Stage_loras_do_not_leak_forward_and_share_one_cached_model_loader()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Scoped_Lora.safetensors");
        JObject document = MakeDocument(MakeClip(
            StageWithLoras(
                fixture.Stage(control: 1, steps: 10),
                Lora("UnitTest_Wan_Scoped_Lora", 0.25)),
            StageWithLoras(
                fixture.Stage("PreviousStage", control: 0.5, steps: 11),
                Lora("UnitTest_Wan_Scoped_Lora", 0.75)),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        UNETLoaderNode loader = Assert.Single(bridge.Graph.NodesOfType<UNETLoaderNode>());
        LoraLoaderModelOnlyNode first = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.StrengthModel.LiteralAsDouble() == 0.25);
        LoraLoaderModelOnlyNode second = Assert.Single(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            node => node.StrengthModel.LiteralAsDouble() == 0.75);
        Assert.Same(loader, first.Model.Connection?.Node);
        Assert.Same(loader, second.Model.Connection?.Node);

        Assert.Same(first, StageSampler(bridge, 0).Model.Connection?.Node);
        Assert.Same(second, StageSampler(bridge, 1).Model.Connection?.Node);
        Assert.Same(loader, StageSampler(bridge, 2).Model.Connection?.Node);
        Assert.Null(generator.UserInput.Get(T2IParamTypes.Loras));

        live.AssertAllLive(
            first, second, StageSampler(bridge, 0), StageSampler(bridge, 1),
            StageSampler(bridge, 2));
        AssertShippable(bridge, workflow, live);
    }
}
