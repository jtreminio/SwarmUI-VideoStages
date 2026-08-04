using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
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
    private const string EndImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42m"
        + "NkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private const string EndImagePayload = $"data:image/png;base64,{EndImageBase64}";

    /// <summary>Frames a 0.6s source clip conforms to on WAN's 4k+1 grid at 24 fps.</summary>
    private const int SourceClipFrames = 17;

    /// <summary>The source window opens one second in.</summary>
    private const int SourceClipStartFrame = 24;

    /// <summary>
    /// A clip that refines uploaded footage instead of generating from noise. Stage 0's default
    /// <c>Generated</c> reference is stripped so the source, not a root donor, is its input.
    /// </summary>
    private static JObject SourceClip(params JObject[] stages) =>
        SourceClip(withFileName: true, stages);

    /// <summary>The file name is optional metadata; the inline payload is what materializes.</summary>
    private static JObject SourceClip(bool withFileName, params JObject[] stages)
    {
        JObject clip = Fixtures.SourceClip(0.6, 1.0, stages);
        if (!withFileName)
        {
            ((JObject)clip["initVideo"]).Remove("fileName");
        }
        return clip;
    }

    private static SwarmFrameWindowNode AssertSourceConformChain(
        WorkflowBridge bridge,
        int expectedFrames = SourceClipFrames) =>
        TypedWorkflowAssertions.AssertSourceConformChain(
            bridge,
            SourceClipStartFrame,
            expectedFrames,
            VideoStagesWorkflowFixture.Width,
            VideoStagesWorkflowFixture.Height);

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

    /// <summary>
    /// A sampler's model branch, unwound: every LoRA applied to it (sorted, since load order is a
    /// separate claim) and the checkpoint loader they all sit on. WAN checkpoints live under
    /// <c>diffusion_models</c>, so that loader is a <c>UNETLoader</c> — reachability from the
    /// sampler proves nothing here, because a refining stage also reaches its predecessor's whole
    /// model branch through the latent it re-encodes.
    /// </summary>
    private static (UNETLoaderNode Loader, string[] Loras) ModelBranchOf(SwarmKSamplerNode sampler)
    {
        List<string> loras = [];
        ComfyNode node = sampler.Model.Connection?.Node;
        while (node is LoraLoaderModelOnlyNode lora)
        {
            loras.Add(lora.LoraName.LiteralAsString());
            node = lora.Model.Connection?.Node;
        }
        return (Assert.IsType<UNETLoaderNode>(node), [.. loras.Order()]);
    }

    /// <summary>
    /// Every node id named in SwarmUI's six-part model-loader cache tuple is still in the shipped
    /// workflow, and the tuple's model half is <paramref name="model"/>.
    /// <para>
    /// This repo has shipped a dead id in that tuple twice — "Node 103 stale dedup" and "Node 101
    /// autogrow" — both times because cleanup pruned a node a cached tuple still named.
    /// <c>VideoGraphHelpers.ReferencesRemovedNode</c> carries a parse branch for this format for
    /// exactly that reason, and only a real graph can test it.
    /// </para>
    /// </summary>
    private static void AssertLoaderTupleIsLive(JObject workflow, string tuple, ComfyNode model)
    {
        string[] parts = tuple.Split(':');
        Assert.Equal(6, parts.Length);
        // Slots 1/3/5 are output indices; the VAE pair is optional and then blank.
        foreach (string nodeId in new[] { parts[0], parts[2], parts[4] }
            .Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            Assert.True(
                workflow[nodeId] is JObject,
                $"Cached loader tuple '{tuple}' names node {nodeId}, which the shipped workflow "
                    + "does not contain.");
        }
        Assert.Equal(model.Id, parts[0]);
    }

    /// <summary>
    /// Exactly one decode reads each of the given samplers, and no other decode reads any of them.
    /// This is the upper bound a chain's spot checks do not give: following one sampler's decode
    /// says nothing about a second decode of the same latent. Core's own base-image decode is not
    /// counted — it reads no stage sampler.
    /// </summary>
    private static void AssertOneDecodePerStage(
        WorkflowBridge bridge,
        params SwarmKSamplerNode[] samplers)
    {
        VAEDecodeNode[] decodes = [.. bridge.Graph.NodesOfType<VAEDecodeNode>()
            .Where(decode => samplers.Contains(decode.Samples.Connection?.Node))];
        Assert.Equal(samplers.Length, decodes.Length);
        Assert.All(
            samplers,
            sampler => Assert.Single(
                decodes,
                decode => decode.Samples.Connection?.Node == sampler));
    }

    /// <summary>The prompt text a conditioning node was encoded from.</summary>
    private static string ConditioningText(ComfyNode conditioning, bool negative)
    {
        ComfyNode encode = conditioning
            .FindInput(negative ? "negative" : "positive")
            .Connection?.Node;
        return encode?.FindInput("text").LiteralAsString();
    }

    /// <summary>
    /// A checkpoint list <see cref="WanWorkflowFixture"/> has no factory for. Both architectures'
    /// support models are installed so the same fixture serves the cross-architecture timelines;
    /// each installer replaces the shared VAE handler, so WAN's VAEs are re-added last.
    /// </summary>
    private sealed class MultiModelFixture : VideoStagesWorkflowFixture
    {
        private MultiModelFixture(IReadOnlyList<string> modelFixturePaths, bool withBaseModel)
            : base(modelFixturePaths, withBaseModel)
        {
        }

        public static MultiModelFixture Create(params string[] modelFixturePaths) =>
            new(modelFixturePaths, withBaseModel: false);

        public static MultiModelFixture CreateWithBaseModel(params string[] modelFixturePaths) =>
            new(modelFixturePaths, withBaseModel: true);

        public override JObject Post(JObject document, Action<JObject> customize = null) =>
            base.Post(document, post =>
            {
                post["clipvisionmodel"] = TestModelFactory.WanClipVisionFileName;
                customize?.Invoke(post);
            });

        protected override void InstallSupportModels()
        {
            TestModelFactory.InstallWanSupportModels();
            TestModelFactory.InstallLtx2SupportModels();
            InstallModel("VAE", CommonModels.Known["wan21-vae"].FileName);
            InstallModel("VAE", CommonModels.Known["wan22-vae"].FileName);
        }

        public override int DefaultSteps => WanWorkflowFixture.Steps;

        public override double DefaultCfgScale => WanWorkflowFixture.CfgScale;

        public override int ExpectedGeneratedFrames => WanWorkflowFixture.GeneratedFrames;
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
            Assert.False(conditioning.ClipVisionOutput.HasValue);
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
        // Absent, not merely unconnected: a literal here would still be a donor.
        Assert.False(latent.StartImage.HasValue);
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
        Assert.False(latent.StartImage.HasValue);
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
        Assert.False(native.StartImage.HasValue);
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
        Assert.False(conditioning.StartImage.HasValue);
        Assert.False(conditioning.ClipVisionStartImage.HasValue);
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
            Assert.False(conditioning.ClipVisionEndImage.HasValue);
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
        Assert.False(conditioning.StartImage.HasValue);
        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("TEFTVA==", upload.ImageBase64.LiteralAsString());
        Assert.NotNull(conditioning.EndImage.Connection);
        // Exactly one: the valid last reference must not draw a warning of its own, which is the
        // whole "does not take the other down with it" claim.
        Assert.Single(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("reference", StringComparison.Ordinal));
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
        // the extension leaves it on the input rather than consuming it. Its payload is what proves
        // that: the two uploads above are the clip's own, and the request's is nowhere in the graph.
        Assert.NotNull(generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null));
        Assert.DoesNotContain(
            uploads,
            upload => upload.ImageBase64.LiteralAsString() == EndImageBase64);

        live.AssertAllLive(conditioning, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    // ---- frame counts -------------------------------------------------------------------

    /// <summary>
    /// An authored clip duration replaces the request's frame count and is snapped onto WAN's 4k+1
    /// grid — 13 frames of footage become 17. The request's own value is left as the user set it,
    /// so a later consumer still sees 25.
    /// <para>
    /// Both 2.1 checkpoints are exercised because the I2V one is the interesting arm: entered as
    /// text-to-video it must build the same bare native latent as the T2V one rather than have
    /// image conditioning invented for it.
    /// </para>
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
        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        // The arm really loaded the checkpoint it names, and neither 2.1 shape conditions on an
        // image it was never given.
        Assert.Equal(
            Path.GetFileName(modelFixturePath),
            ModelBranchOf(stage).Loader.UnetName.LiteralAsString());
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<CLIPVisionEncodeNode>());

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
        // The source replaces core's video root; core's base image pass survives it, protected by
        // core's own image save.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

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
        Assert.Equal(
            Path.GetFileName(modelFixturePath),
            Assert.IsType<UNETLoaderNode>(prompt.Model.Connection?.Node)
                .UnetName.LiteralAsString());
        Assert.Same(prompt, persisted.Model.Connection?.Node);
        SwarmKSamplerNode stageSampler = StageSampler(bridge, 0);
        Assert.Same(persisted, stageSampler.Model.Connection?.Node);

        // The arms are different graph shapes, which the LoRA chain alone would never notice: 14B
        // conditions through WanImageToVideo, 5B through its own native latent.
        bool is5b = modelFixturePath == WanWorkflowFixture.Wan22Ti2v5bFixturePath;
        Assert.Equal(is5b ? 0 : 1, bridge.Graph.NodesOfType<WanImageToVideoNode>().Count);
        Assert.Equal(is5b ? 1 : 0, bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>().Count);

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
            Assert.False(latent.StartImage.HasValue);
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

        // The unscoped third stage leaves the loader cached, so a later consumer would reuse the
        // tuple as-is — every id in it must survive the cleanup the earlier stages triggered.
        AssertLoaderTupleIsLive(
            workflow,
            generator.NodeHelpers[$"modelloader_{fixture.Model.Name}_image2video"],
            loader);

        live.AssertAllLive(
            first, second, StageSampler(bridge, 0), StageSampler(bridge, 1),
            StageSampler(bridge, 2));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Prompt LoRA confinements pick out exactly the passes their selector names: a bare
    /// <c>&lt;videoclip&gt;</c> reaches every generating stage, <c>[0,1]</c> only that one stage,
    /// and <c>[1]</c> only the second clip. Each stage's branch is read by unwinding its model
    /// input, since every later stage also *reaches* its predecessor's LoRAs through the latent it
    /// refines.
    /// </summary>
    [Fact]
    public async Task Prompt_lora_confinements_select_exactly_the_passes_they_name()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        foreach (string name in new[] { "Bare", "Stage", "ClipWide" })
        {
            fixture.InstallModel("LoRA", $"UnitTest_Wan_{name}.safetensors");
        }
        JObject document = MakeDocument(
            MakeClip(
                fixture.Stage(steps: 10),
                fixture.Stage("PreviousStage", control: 0.5, steps: 11)),
            MakeClip(fixture.Stage(steps: 12)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            document,
            post => post["prompt"] =
                "global <videoclip><lora:UnitTest_Wan_Bare:0.2>"
                + " <videoclip[0,1]><lora:UnitTest_Wan_Stage:0.3>"
                + " <videoclip[1]><lora:UnitTest_Wan_ClipWide:0.4>");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        (UNETLoaderNode loader, string[] first) = ModelBranchOf(StageSampler(bridge, 0));
        (UNETLoaderNode secondLoader, string[] second) = ModelBranchOf(StageSampler(bridge, 1));
        (UNETLoaderNode thirdLoader, string[] third) = ModelBranchOf(StageSampler(bridge, 2));
        Assert.Same(loader, secondLoader);
        Assert.Same(loader, thirdLoader);
        Assert.Equal(["UnitTest_Wan_Bare.safetensors"], first);
        Assert.Equal(
            ["UnitTest_Wan_Bare.safetensors", "UnitTest_Wan_Stage.safetensors"],
            second);
        Assert.Equal(
            ["UnitTest_Wan_Bare.safetensors", "UnitTest_Wan_ClipWide.safetensors"],
            third);

        live.AssertAllLive(
            loader, StageSampler(bridge, 0), StageSampler(bridge, 1), StageSampler(bridge, 2));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A LoRA confined to a passthrough stage never loads — the stage has no pass to apply it to —
    /// and the unscoped stage after it samples straight off the shared checkpoint loader. The
    /// generating stage's own LoRA is the control that the confinement syntax works at all.
    /// </summary>
    [Fact]
    public async Task A_prompt_lora_confined_to_a_passthrough_stage_never_loads()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Generating.safetensors");
        fixture.InstallModel("LoRA", "UnitTest_Wan_Passthrough.safetensors");
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: 0, steps: 11),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            document,
            post => post["prompt"] =
                "global <videoclip[0,0]><lora:UnitTest_Wan_Generating:0.3>"
                + " <videoclip[0,1]><lora:UnitTest_Wan_Passthrough:0.4>");
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        (UNETLoaderNode loader, string[] first) = ModelBranchOf(StageSampler(bridge, 0));
        (UNETLoaderNode lastLoader, string[] last) = ModelBranchOf(StageSampler(bridge, 2));
        Assert.Equal(["UnitTest_Wan_Generating.safetensors"], first);
        Assert.Empty(last);
        Assert.Same(loader, lastLoader);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<LoraLoaderModelOnlyNode>(),
            lora => lora.LoraName.LiteralAsString() == "UnitTest_Wan_Passthrough.safetensors");

        live.AssertAllLive(loader, StageSampler(bridge, 0), StageSampler(bridge, 2));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A stage naming a LoRA that does not exist refuses the whole request, and everything the
    /// stage borrowed from the host is handed back on the way out: the request's LoRA lists, the
    /// stage's parameter section, and the cached model loader. The prompt-LoRA arm makes the
    /// rollback nested — one scope already applied when the next one throws.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_stage_lora_refuses_the_request_and_hands_back_host_state(
        bool withPromptLora)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_Wan_Present.safetensors");
        JObject stage = StageWithLoras(
            fixture.Stage(steps: 10),
            Lora("UnitTest_Wan_Missing", 0.45));
        WorkflowGenerator captured = null;
        List<string> lorasBeforeStages = null;

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(stage)),
                    post => post["prompt"] = withPromptLora
                        ? "global <videoclip[0,0]><lora:UnitTest_Wan_Present:0.4>"
                        : "global"),
                extraSteps:
                [
                    new(g =>
                    {
                        captured = g;
                        lorasBeforeStages = g.UserInput.Get(T2IParamTypes.Loras);
                    }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01),
                ]));

        Assert.Contains("UnitTest_Wan_Missing", error.Message);
        Assert.NotNull(captured);
        // Null-for-null, not []-for-null: the no-prompt-LoRA arm had no list at all, and the
        // stage's borrowed one must be removed rather than left behind as an empty list.
        Assert.Equal<IEnumerable<string>>(
            lorasBeforeStages,
            captured.UserInput.Get(T2IParamTypes.Loras));
        Assert.DoesNotContain(
            $"modelloader_{fixture.Model.Name}_image2video",
            captured.NodeHelpers.Keys);
        Assert.DoesNotContain(
            VideoStagesExtension.SectionIdForStage(0),
            captured.UserInput.SectionParamOverrides.Keys);
    }

    // ---- stage handoffs -----------------------------------------------------------------

    /// <summary>
    /// Two stages on distinct checkpoints run as two ordinary passes joined by a decode and a
    /// re-encode: each keeps its own sampler settings and its own section of the prompt, and the
    /// second conditions on the first's decoded first frame. The pair is deliberately not a
    /// high-then-low continuation — that needs a high-noise predecessor, which stage 0 is not.
    /// </summary>
    [Fact]
    public async Task Two_stages_on_distinct_checkpoints_hand_off_through_a_decode_and_re_encode()
    {
        using MultiModelFixture fixture = MultiModelFixture.CreateWithBaseModel(
            WanWorkflowFixture.Wan22I2v14bFixturePath,
            WanWorkflowFixture.Wan22I2v14bLowNoiseFixturePath);
        JObject document = MakeDocument(MakeClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 10, cfgScale: 4),
            MakeStage(
                fixture.Models[1].Name,
                "PreviousStage",
                control: 0.35,
                steps: 12,
                cfgScale: 6.5,
                sampler: "dpmpp_2m",
                scheduler: "karras")));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document,
                    post => post["prompt"] =
                        "global <videoclip[0,0]>first-stage-prompt"
                        + " <videoclip[0,1]>second-stage-prompt"));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(10, first.Steps.LiteralAsInt());
        Assert.Equal(4.0, first.Cfg.LiteralAsDouble());
        Assert.Equal("euler", first.SamplerName.LiteralAsString());
        Assert.Equal("normal", first.Scheduler.LiteralAsString());
        Assert.Equal(0, first.StartAtStep.LiteralAsInt());
        Assert.Equal(12, second.Steps.LiteralAsInt());
        Assert.Equal(6.5, second.Cfg.LiteralAsDouble());
        Assert.Equal("dpmpp_2m", second.SamplerName.LiteralAsString());
        Assert.Equal("karras", second.Scheduler.LiteralAsString());
        // control 0.35 over 12 steps starts at floor(12 * 0.65).
        Assert.Equal(7, second.StartAtStep.LiteralAsInt());

        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bFixturePath),
            ModelBranchOf(first).Loader.UnetName.LiteralAsString());
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bLowNoiseFixturePath),
            ModelBranchOf(second).Loader.UnetName.LiteralAsString());

        VAEEncodeNode handoff = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        VAEDecodeNode firstDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => decode.Samples.Connection?.Node == first);
        Assert.True(ReachesUpstream(bridge, handoff, firstDecode.Id));
        WanImageToVideoNode secondConditioning = Assert.IsType<WanImageToVideoNode>(
            second.Positive.Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge, secondConditioning.StartImage.Connection?.Node, firstDecode.Id));
        Assert.Equal(
            "first-stage-prompt",
            ConditioningText(first.Positive.Connection?.Node, negative: false));
        Assert.Equal(
            "second-stage-prompt",
            ConditioningText(secondConditioning, negative: false));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);

        live.AssertAllLive(handoff, firstDecode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    // ---- high/low noise continuation ----------------------------------------------------

    /// <summary>
    /// A high-noise stage followed by a low-noise one is a single sampling run split in two: the
    /// low stage takes the high sampler's leftover-noise latent directly, with no decode and
    /// re-encode between them. Each half keeps its own model, LoRAs and prompt section — the two
    /// prompts are composed into core's <c>&lt;video&gt;</c>/<c>&lt;videoswap&gt;</c> pair, which
    /// is the only thing that ever populates the video-swap section.
    /// <para>
    /// The split step is <c>floor(steps * (1 - control))</c> of the low stage, pinned as literals.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.35, 5)]
    [InlineData(0.5, 4)]
    [InlineData(0.8, 1)]
    public async Task High_to_low_noise_stages_continue_one_sampling_run_without_a_vae_boundary(
        double lowControl,
        int splitStep)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        foreach (string name in new[]
        {
            "UnitTest_Wan_High_Prompt", "UnitTest_Wan_High_Persisted",
            "UnitTest_Wan_Low_Prompt", "UnitTest_Wan_Low_Persisted",
        })
        {
            fixture.InstallModel("LoRA", $"{name}.safetensors");
        }
        JObject document = MakeDocument(MakeClip(
            StageWithLoras(
                MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8, cfgScale: 4),
                Lora("UnitTest_Wan_High_Persisted", 0.3)),
            StageWithLoras(
                MakeStage(
                    fixture.LowNoiseModel.Name,
                    "PreviousStage",
                    control: lowControl,
                    steps: 8,
                    cfgScale: 6.5,
                    sampler: "dpmpp_2m"),
                Lora("UnitTest_Wan_Low_Persisted", 0.7))));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["prompt"] =
                        "global <videoclip[0,0]>high-stage-prompt"
                        + " <lora:UnitTest_Wan_High_Prompt:0.2>"
                        + " <videoclip[0,1]>low-stage-prompt"
                        + " <lora:UnitTest_Wan_Low_Prompt:0.8>";
                    post["outputintermediateimages"] = true;
                }),
                extraSteps:
                [
                    // Values only core's own video-swap pass reads. The continuation borrows core's
                    // IsImageToVideoSwap machinery and drives it from the stages instead, so it
                    // must hand the request's section back exactly as it found it. No POST field
                    // populates this section — <videoswap> composes prompt text into it, not
                    // sampler settings.
                    new(g =>
                    {
                        g.UserInput.Set(
                            T2IParamTypes.Steps, 31, T2IParamInput.SectionID_VideoSwap);
                        g.UserInput.Set(
                            T2IParamTypes.CFGScale, 9, T2IParamInput.SectionID_VideoSwap);
                    }, Constants.WorkflowStepPriority.RunConfiguredStages - 0.01),
                ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        Assert.Equal(4.0, high.Cfg.LiteralAsDouble());
        Assert.Equal("euler", high.SamplerName.LiteralAsString());
        Assert.Equal(0, high.StartAtStep.LiteralAsInt());
        Assert.Equal(splitStep, high.EndAtStep.LiteralAsInt());
        Assert.Equal("enable", high.AddNoise.LiteralAsString());
        Assert.Equal("enable", high.ReturnWithLeftoverNoise.LiteralAsString());
        Assert.Equal(6.5, low.Cfg.LiteralAsDouble());
        Assert.Equal("dpmpp_2m", low.SamplerName.LiteralAsString());
        Assert.Equal(splitStep, low.StartAtStep.LiteralAsInt());
        Assert.Equal(10000, low.EndAtStep.LiteralAsInt());
        Assert.Equal("disable", low.AddNoise.LiteralAsString());
        Assert.Equal("disable", low.ReturnWithLeftoverNoise.LiteralAsString());
        Assert.Same(high, low.LatentImage.Connection?.Node);
        // The re-encode every ordinary two-stage clip carries is exactly what a continuation
        // avoids — Two_stages_on_distinct_checkpoints... is the control that one normally exists.
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());

        (UNETLoaderNode highLoader, string[] highLoras) = ModelBranchOf(high);
        (UNETLoaderNode lowLoader, string[] lowLoras) = ModelBranchOf(low);
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bHighNoiseFixturePath),
            highLoader.UnetName.LiteralAsString());
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bLowNoiseFixturePath),
            lowLoader.UnetName.LiteralAsString());
        Assert.Equal(
            ["UnitTest_Wan_High_Persisted.safetensors", "UnitTest_Wan_High_Prompt.safetensors"],
            highLoras);
        Assert.Equal(
            ["UnitTest_Wan_Low_Persisted.safetensors", "UnitTest_Wan_Low_Prompt.safetensors"],
            lowLoras);

        Assert.Equal(
            "high-stage-prompt",
            ConditioningText(high.Positive.Connection?.Node, negative: false));
        Assert.Equal(
            "low-stage-prompt",
            ConditioningText(low.Positive.Connection?.Node, negative: false));

        VAEDecodeNode highDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => decode.Samples.Connection?.Node == high);
        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(2, saves.Length);
        Assert.Single(saves, save => save.Images.Connection?.Node == highDecode);
        Assert.Same(
            low,
            Assert.IsType<VAEDecodeNode>(live.FinalVideoSave().Images.Connection?.Node)
                .Samples.Connection?.Node);
        Assert.False(generator.IsImageToVideoSwap);
        Assert.Equal(
            31,
            generator.UserInput.GetNullable(
                T2IParamTypes.Steps, T2IParamInput.SectionID_VideoSwap, false));
        Assert.Equal(
            9,
            generator.UserInput.GetNullable(
                T2IParamTypes.CFGScale, T2IParamInput.SectionID_VideoSwap, false));

        live.AssertAllLive(high, low, highLoader, lowLoader);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// A source clip enters the continuation the same way a generated one does: the high stage
    /// encodes the conformed footage and the low stage picks up its latent, so the whole clip
    /// decodes exactly once.
    /// </summary>
    [Fact]
    public async Task An_init_video_high_to_low_pair_shares_one_source_sampling_run()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject document = MakeDocument(SourceClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8),
            MakeStage(
                fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: 8)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        // The pair plus core's base image pass.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Same(high, low.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, high.LatentImage.Connection?.Node, window.Id));
        VAEDecodeNode decode = BaseImage(bridge, low);
        Assert.Equal(SourceClipFrames, generator.CurrentMedia.Frames);

        live.AssertAllLive(window, high, low, decode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The request's global end image reaches the continuation's terminal low stage. Core rebuilds
    /// the full conditioning for its swap pass, so both halves of the one sampling run condition on
    /// the same end frame — consistent, since they denoise a single trajectory. The pair still
    /// shares its latent with no VAE boundary.
    /// </summary>
    [Fact]
    public async Task A_high_to_low_continuation_puts_the_global_end_image_on_the_low_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject document = MakeDocument(MakeClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8),
            MakeStage(
                fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: 8)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            document,
            post => post["videoendimage"] = EndImagePayload);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        Assert.Same(high, low.LatentImage.Connection?.Node);
        // Core rebuilds the conditioning for its swap pass; with no per-stage prompt the two are
        // identical, so its post-cleanup collapses them onto one node.
        WanFirstLastFrameToVideoNode terminal = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Same(terminal, low.Positive.Connection?.Node);
        Assert.Same(terminal, high.Positive.Connection?.Node);
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(EndImageBase64, endImage.ImageBase64.LiteralAsString());
        Assert.Same(
            endImage,
            Assert.IsType<ImageScaleNode>(terminal.EndImage.Connection?.Node)
                .Image.Connection?.Node);
        // The end slot only: the start stays the host base image, so the continuation still opens
        // from what the request generated rather than from the frame it is aiming at.
        Assert.True(ReachesUpstream(
            bridge, terminal.StartImage.Connection?.Node, fixture.BaseSampler(bridge).Id));
        Assert.False(ReachesUpstream(bridge, terminal.StartImage.Connection?.Node, endImage.Id));
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());

        live.AssertAllLive(endImage, terminal, high, low);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The continuation composes both stages' prompts into one <c>&lt;video&gt;/&lt;videoswap&gt;</c>
    /// pair, which cannot express "one half has text and the other does not". Rather than silently
    /// dropping a prompt, planning abandons the continuation: two ordinary passes joined by a
    /// re-encode, the low stage adding its own noise from its own start step.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_half_blank_prompt_falls_back_to_two_ordinary_stages(bool blankIsNegative)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject document = MakeDocument(MakeClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 8),
            MakeStage(
                fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: 8)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["prompt"] = blankIsNegative
                        ? "global <videoclip[0,0]>high-positive <videoclip[0,1]>low-positive"
                        : "global <videoclip[0,0]>high-stage <videoclip[0,1]>";
                    if (!blankIsNegative)
                    {
                        return;
                    }
                    post["negativeprompt"] = "<videoclip[0,0]><videoclip[0,1]>low-negative";
                    post["zeronegative"] = true;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode high = StageSampler(bridge, 0);
        SwarmKSamplerNode low = StageSampler(bridge, 1);
        Assert.Equal(10000, high.EndAtStep.LiteralAsInt());
        Assert.Equal("disable", high.ReturnWithLeftoverNoise.LiteralAsString());
        // control 0.5 over 8 steps.
        Assert.Equal(4, low.StartAtStep.LiteralAsInt());
        Assert.Equal("enable", low.AddNoise.LiteralAsString());
        VAEEncodeNode handoff = Assert.IsType<VAEEncodeNode>(low.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, handoff, high.Id));

        if (blankIsNegative)
        {
            // An empty negative is zeroed rather than encoded; keeping that is the whole reason
            // the fallback exists.
            Assert.Equal(
                "ConditioningZeroOut",
                high.Negative.Connection?.Node?.FindInput("negative")
                    .Connection?.Node?.ClassTypeName);
            Assert.Equal(
                "low-negative",
                ConditioningText(low.Negative.Connection?.Node, negative: true));
        }
        else
        {
            Assert.Equal(
                "high-stage",
                ConditioningText(high.Positive.Connection?.Node, negative: false));
            Assert.True(string.IsNullOrWhiteSpace(
                ConditioningText(low.Positive.Connection?.Node, negative: false)));
        }
        Assert.False(generator.IsImageToVideoSwap);

        live.AssertAllLive(handoff, high, low);
        AssertShippable(bridge, workflow, live);
    }

    // ---- stage upscales -----------------------------------------------------------------

    /// <summary>
    /// A stage's pixel upscale resizes the decoded handoff before the stage runs, so the next
    /// sampler's latent and its conditioning frame both come from the enlarged video and the clip
    /// publishes at the new size.
    /// </summary>
    [Fact]
    public async Task A_pixel_upscale_resizes_the_decoded_handoff_before_the_next_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(steps: 10),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        VAEDecodeNode handoffDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => decode.Samples.Connection?.Node == first);
        ImageScaleNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Image.Connection?.Node == handoffDecode);
        Assert.Equal(768, upscale.Width.LiteralAsInt());
        Assert.Equal(768, upscale.Height.LiteralAsInt());
        Assert.Equal("lanczos", upscale.UpscaleMethod.LiteralAsString());

        VAEEncodeNode reEncode = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, reEncode, upscale.Id));
        WanImageToVideoNode conditioning = Assert.IsType<WanImageToVideoNode>(
            second.Positive.Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge, conditioning.StartImage.Connection?.Node, upscale.Id));
        Assert.Equal(768, generator.CurrentMedia.Width);
        Assert.Equal(768, generator.CurrentMedia.Height);

        live.AssertAllLive(upscale, reEncode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A passthrough stage that only upscales adds no sampler of its own: the resize is the clip's
    /// published output.
    /// </summary>
    [Fact]
    public async Task A_pixel_upscale_on_a_passthrough_stage_is_the_clips_output()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(steps: 10),
            fixture.Stage(
                "PreviousStage",
                control: 0,
                upscale: 1.5,
                upscaleMethod: "pixel-bicubic",
                steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        // Core's base pass plus this one stage; the passthrough contributes none.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        ImageScaleNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.UpscaleMethod.LiteralAsString() == "bicubic");
        Assert.Equal(768, upscale.Width.LiteralAsInt());
        Assert.Equal(768, upscale.Height.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, upscale, stage.Id));
        Assert.Same(upscale, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Equal(768, generator.CurrentMedia.Width);
        Assert.Equal(768, generator.CurrentMedia.Height);

        live.AssertAllLive(upscale, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// WAN has no latent-space upscaler, so a stage authoring one is warned about and dropped
    /// rather than being passed to <c>ImageScale</c> as an upscale method it would reject. The
    /// pixel-upscale tests above are the control that a real resize does reach the graph.
    /// </summary>
    [Theory]
    [InlineData("latent-bislerp", "bislerp")]
    [InlineData("latentmodel-unit-upscaler.safetensors", "unit-upscaler.safetensors")]
    public async Task A_latent_upscale_is_warned_about_and_emits_no_pixel_scaler(
        string upscaleMethod,
        string rejectedPixelMethod)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(steps: 10),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: upscaleMethod,
                steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.UpscaleMethod.LiteralAsString() == rejectedPixelMethod);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 768);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(
                $"uses latent upscale mode '{upscaleMethod}'", StringComparison.Ordinal));
        Assert.Equal(512, generator.CurrentMedia.Width);

        live.AssertAllLive(StageSampler(bridge, 0), second);
        AssertShippable(bridge, workflow, live);
    }

    // ---- init-video clips ---------------------------------------------------------------

    /// <summary>
    /// A partially-controlled source stage reads the conformed footage twice, for two different
    /// jobs: one frame for the conditioning donor and the whole window for the latent it refines.
    /// They are separate nodes — folding them together would either encode one frame or condition
    /// on all of them. The request's global trim then shortens the published result, not the
    /// window.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_partial_source_stage_conditions_on_one_frame_and_encodes_the_window(
        bool withFileName)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(
            SourceClip(withFileName, fixture.Stage(control: 0.5, steps: 10)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["trimvideostartframes"] = 4));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        // The stage plus core's base image pass.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        // control 0.5 over 10 steps.
        Assert.Equal(5, stage.StartAtStep.LiteralAsInt());

        VAEEncodeNode encode = Assert.IsType<VAEEncodeNode>(stage.LatentImage.Connection?.Node);
        ImageFromBatchNode encoded = Assert.IsType<ImageFromBatchNode>(
            encode.Pixels.Connection?.Node);
        Assert.Equal(0, encoded.BatchIndex.LiteralAsInt());
        Assert.Equal(SourceClipFrames, encoded.Length.LiteralAsInt());
        WanImageToVideoNode conditioning = Assert.IsType<WanImageToVideoNode>(
            stage.Positive.Connection?.Node);
        ImageFromBatchNode donor = Assert.IsType<ImageFromBatchNode>(
            conditioning.StartImage.Connection?.Node);
        Assert.Equal(0, donor.BatchIndex.LiteralAsInt());
        Assert.Equal(1, donor.Length.LiteralAsInt());
        Assert.NotSame(donor, encoded);
        Assert.False(ReachesUpstream(bridge, encoded, donor.Id));
        Assert.True(ReachesUpstream(bridge, encoded, window.Id));
        Assert.True(ReachesUpstream(bridge, donor, window.Id));

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.Equal(SourceClipFrames - 4, generator.CurrentMedia.Frames);
        Assert.Same(trim, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Null(live.PublishedAudio());
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        // Silent footage stays silent: no bed is synthesised for it.
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());

        live.AssertAllLive(window, encode, donor, stage, trim);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// At full control there is nothing to refine: the source contributes only its first frame as
    /// the conditioning donor, and no encode of the footage is built at all.
    /// </summary>
    [Fact]
    public async Task A_full_control_source_stage_uses_only_the_sources_first_frame()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(SourceClip(fixture.Stage(control: 1, steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(0, stage.StartAtStep.LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        WanImageToVideoNode conditioning = Assert.IsType<WanImageToVideoNode>(
            stage.Positive.Connection?.Node);
        Assert.Same(conditioning.Latent, stage.LatentImage.Connection);
        Assert.True(ReachesUpstream(
            bridge, conditioning.StartImage.Connection?.Node, window.Id));
        Assert.Null(generator.CurrentMedia.AttachedAudio);

        live.AssertAllLive(window, conditioning, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Core really does build a whole WAN video root before the timeline runs — its own sampler,
    /// its published media and its save node. A source clip replaces all of it: the sampler and the
    /// media are pruned, and the save is retargeted rather than duplicated, so the request ends up
    /// with exactly one video output and it is the source's.
    /// </summary>
    [Fact]
    public async Task A_source_clip_prunes_cores_video_root_and_reuses_its_save()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        string coreSamplerId = null;
        string coreMediaId = null;
        string coreSaveId = null;
        WorkflowGenerator generator = null;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.ImageToVideoPost(
                MakeDocument(SourceClip(fixture.Stage(control: 0.5, steps: 10)))),
            extraSteps:
            [
                new(g =>
                {
                    using WorkflowBridge core = WorkflowBridge.Create(g.Workflow);
                    coreSamplerId = Assert.Single(
                        core.Graph.NodesOfType<SwarmKSamplerNode>(),
                        sampler => sampler.NoiseSeed.LiteralAsLong()
                            == VideoStagesWorkflowFixture.StageSeed(0)).Id;
                    coreSaveId = Assert.Single(
                        core.Graph.NodesOfType<SwarmSaveAnimationWSNode>()).Id;
                    coreMediaId = core.ResolvePath(g.CurrentMedia.Path)?.Node.Id;
                }, Constants.WorkflowStepPriority.DropCoreImageToVideoOutput - 0.01),
                new(g => generator = g, double.MaxValue),
            ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.NotNull(coreMediaId);
        Assert.Null(workflow[coreSamplerId]);
        Assert.Null(workflow[coreMediaId]);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.NotEqual(coreSamplerId, stage.Id);
        VAEEncodeNode encode = Assert.IsType<VAEEncodeNode>(stage.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, encode, window.Id));

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(coreSaveId, save.Id);
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, stage.Id));
        Assert.False(save.Audio.HasValue);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());

        live.AssertAllLive(window, encode, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A source clip whose only stage is a passthrough contributes no sampler of its own: the
    /// conformed, trimmed footage is the output. Its window is 16 frames rather than the
    /// <see cref="SourceClipFrames"/> a generating stage takes — nothing generates, so WAN's 4k+1
    /// grid does not apply.
    /// </summary>
    [Fact]
    public async Task A_source_only_passthrough_publishes_the_trimmed_footage_without_a_sampler()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(SourceClip(fixture.Stage(control: 0, steps: 10))),
                    post => post["trimvideostartframes"] = 4));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge, expectedFrames: 16);
        // Nothing in the timeline samples; core's base image pass is all that is left.
        Assert.Same(
            fixture.BaseSampler(bridge),
            Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>()));
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.True(ReachesUpstream(bridge, trim, window.Id));
        Assert.Equal(12, generator.CurrentMedia.Frames);
        Assert.Same(trim, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Null(live.PublishedAudio());
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());

        live.AssertAllLive(window, trim);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A passthrough stage 0 still counts as a stage for publication: with intermediates on, the
    /// untouched source is saved alongside the refined result, and the refining stage encodes the
    /// source rather than a decode of some earlier pass.
    /// </summary>
    [Fact]
    public async Task A_source_passthrough_then_refine_publishes_the_untouched_source_too()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(SourceClip(
            fixture.Stage(control: 0, steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, steps: 10)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["outputintermediateimages"] = true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 1);
        // The refining stage plus core's base image pass.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        VAEEncodeNode encode = Assert.IsType<VAEEncodeNode>(stage.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, encode, window.Id));

        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(2, saves.Length);
        Assert.Single(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, window.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, stage.Id));
        Assert.True(ReachesUpstream(
            bridge, live.FinalVideoSave().Images.Connection?.Node, stage.Id));
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Empty(bridge.Graph.NodesOfType<TrimAudioDurationNode>());

        live.AssertAllLive(window, encode, stage);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// A passthrough in the middle of a clip is transparent, not terminal: the stage after it
    /// refines the stage before it. It still publishes an intermediate of its own, so the
    /// unmodified output is saved twice — once for each stage that claims it.
    /// </summary>
    [Fact]
    public async Task A_mid_clip_passthrough_hands_the_previous_stage_to_the_next_refine()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 8),
            fixture.Stage("PreviousStage", control: 0, steps: 9),
            fixture.Stage("PreviousStage", control: 0.5, steps: 10)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["outputintermediateimages"] = true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode third = StageSampler(bridge, 2);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => sampler.NoiseSeed.LiteralAsLong()
                == VideoStagesWorkflowFixture.StageSeed(1));
        VAEEncodeNode handoff = Assert.IsType<VAEEncodeNode>(third.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, handoff, first.Id));

        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(3, saves.Length);
        Assert.Equal(
            2,
            saves.Count(save =>
                ReachesUpstream(bridge, save.Images.Connection?.Node, first.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, third.Id)));
        Assert.True(ReachesUpstream(
            bridge, live.FinalVideoSave().Images.Connection?.Node, third.Id));
        Assert.Null(generator.CurrentMedia.AttachedAudio);

        live.AssertAllLive(handoff, first, third);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }

    /// <summary>
    /// A generated clip and a source clip in one timeline keep their inputs apart in both
    /// directions, whichever comes first: the generated clip never conditions on the footage, and
    /// the source clip's latent never traces back to the generated clip's sampler.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_generated_and_a_source_clip_keep_their_inputs_apart(bool sourceFirst)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject generated = MakeClip(0.6, fixture.Stage(control: 1, steps: 8));
        JObject source = SourceClip(fixture.Stage(control: 0.5, steps: 10));
        JObject document = sourceFirst
            ? MakeDocument(source, generated)
            : MakeDocument(generated, source);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode generatedSampler = StageSampler(bridge, sourceFirst ? 1 : 0);
        SwarmKSamplerNode sourceSampler = StageSampler(bridge, sourceFirst ? 0 : 1);
        Assert.False(ReachesUpstream(
            bridge,
            Assert.IsType<WanImageToVideoNode>(generatedSampler.Positive.Connection?.Node)
                .StartImage.Connection?.Node,
            window.Id));
        VAEEncodeNode sourceLatent = Assert.IsType<VAEEncodeNode>(
            sourceSampler.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, sourceLatent, window.Id));
        Assert.False(ReachesUpstream(bridge, sourceLatent, generatedSampler.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Same(merged, bridge.ResolvePath(generator.CurrentMedia.Path)?.Node);
        Assert.Same(merged, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Null(generator.CurrentMedia.AttachedAudio);

        live.AssertAllLive(window, sourceLatent, generatedSampler, sourceSampler, merged);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A later stage at full control regenerates rather than refines: it conditions on the previous
    /// stage's decoded first frame and samples from the conditioning's own latent.
    /// <para>
    /// The absence of a re-encode is weaker here than it looks — core's post-cleanup prunes
    /// unreachable nodes, so a re-encode that was built and then stranded would leave no trace. The
    /// load-bearing half is that the sampler's latent comes from the conditioning node.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_later_full_control_stage_regenerates_from_the_previous_first_frame()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 8),
            fixture.Stage("PreviousStage", control: 1, steps: 10)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(0, second.StartAtStep.LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());
        WanImageToVideoNode conditioning = Assert.IsType<WanImageToVideoNode>(
            second.Positive.Connection?.Node);
        Assert.Same(conditioning.Latent, second.LatentImage.Connection);
        VAEDecodeNode handoff = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => decode.Samples.Connection?.Node == first);
        Assert.True(ReachesUpstream(
            bridge, conditioning.StartImage.Connection?.Node, handoff.Id));

        live.AssertAllLive(handoff, conditioning, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A control that quantizes to a single step is still a refine, not a regenerate: the stage
    /// starts at step 1 and encodes the previous stage's decoded video.
    /// </summary>
    [Fact]
    public async Task A_one_step_partial_control_still_encodes_the_previous_video()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(steps: 8),
            fixture.Stage("PreviousStage", control: 0.87, steps: 8)));

        JObject workflow = await fixture.GenerateImageToVideoAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        // floor(8 * (1 - 0.87)) == 1.
        Assert.Equal(1, second.StartAtStep.LiteralAsInt());
        VAEEncodeNode encode = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, encode, first.Id));

        live.AssertAllLive(encode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Three chained stages each publish their own intermediate, but the request's frame trim
    /// belongs to the finished timeline: exactly one trim node, on the last stage's output, and it
    /// is what the published save reads.
    /// </summary>
    [Fact]
    public async Task Three_chained_stages_publish_intermediates_and_only_the_last_is_trimmed()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, steps: 10),
            fixture.Stage("PreviousStage", control: 0.25, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["outputintermediateimages"] = true;
                    post["trimvideostartframes"] = 4;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        SwarmKSamplerNode third = StageSampler(bridge, 2);
        // floor(10 * 0.5) and floor(12 * 0.75).
        Assert.Equal(5, second.StartAtStep.LiteralAsInt());
        Assert.Equal(9, third.StartAtStep.LiteralAsInt());
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node),
            first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(third.LatentImage.Connection?.Node),
            second.Id));
        // The chain's whole VAE census: one decode per stage and no more, and the two re-encodes
        // the two handoffs need. Nothing else may add a round trip.
        AssertOneDecodePerStage(bridge, first, second, third);
        Assert.Equal(2, bridge.Graph.NodesOfType<VAEEncodeNode>().Count);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(3, saves.Length);
        Assert.Same(trim, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Equal(2, saves.Count(save => save.Images.Connection?.Node is VAEDecodeNode));
        Assert.All(saves, save => Assert.Equal(24.0, save.Fps.LiteralAsDouble()));
        Assert.Equal(21, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertAllLive(trim, first, second, third);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }

    /// <summary>
    /// The request's global end image goes to the clip's last generating stage and nowhere else:
    /// the opening stage keeps plain image-to-video conditioning off the host base image. Core's
    /// own WAN video root, which would carry a third conditioning node, is pruned.
    /// <para>
    /// The 2.1 arm exists because 2.1 additionally CLIP-vision encodes both ends; without that
    /// assertion it would run the same checks as its 2.2 sibling and pin nothing about the model.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, 0.5, false)]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, 1, false)]
    [InlineData(WanWorkflowFixture.Wan21I2v14bFixturePath, 1, true)]
    public async Task The_global_end_image_belongs_to_the_clips_terminal_generating_stage(
        string modelFixturePath,
        double terminalControl,
        bool expectsClipVision)
    {
        using WanWorkflowFixture fixture =
            WanWorkflowFixture.CreateWithBaseModel(modelFixturePath);
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: terminalControl, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["videoendimage"] = EndImagePayload;
                    // Non-square on purpose: ImageScale defaults to 512x512, so the framing
                    // assertion below would hold at the fixture's own resolution either way.
                    post["width"] = 768;
                    post["height"] = 448;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode terminal = StageSampler(bridge, 1);
        WanImageToVideoNode opening = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        WanFirstLastFrameToVideoNode last = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Same(opening.Positive, first.Positive.Connection);
        Assert.Same(last.Positive, terminal.Positive.Connection);

        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(EndImageBase64, endImage.ImageBase64.LiteralAsString());
        ImageScaleNode framing = Assert.IsType<ImageScaleNode>(last.EndImage.Connection?.Node);
        Assert.Same(endImage, framing.Image.Connection?.Node);
        Assert.Equal(768, framing.Width.LiteralAsInt());
        Assert.Equal(448, framing.Height.LiteralAsInt());
        Assert.Equal("lanczos", framing.UpscaleMethod.LiteralAsString());

        // The other half of "and nowhere else": the end image claims the end slot only. The start
        // stays the host base image, which both conditioning nodes share.
        SwarmKSamplerNode basePass = fixture.BaseSampler(bridge);
        Assert.True(ReachesUpstream(bridge, last.StartImage.Connection?.Node, basePass.Id));
        Assert.False(ReachesUpstream(bridge, last.StartImage.Connection?.Node, endImage.Id));
        Assert.True(ReachesUpstream(bridge, opening.StartImage.Connection?.Node, basePass.Id));
        Assert.False(ReachesUpstream(bridge, opening.StartImage.Connection?.Node, endImage.Id));

        if (expectsClipVision)
        {
            Assert.True(ReachesUpstream(
                bridge, last.ClipVisionEndImage.Connection?.Node, endImage.Id));
            Assert.True(ReachesUpstream(
                bridge, last.ClipVisionStartImage.Connection?.Node, basePass.Id));
        }
        else
        {
            Assert.False(last.ClipVisionEndImage.HasValue);
            Assert.False(last.ClipVisionStartImage.HasValue);
            Assert.Empty(bridge.Graph.NodesOfType<CLIPVisionEncodeNode>());
        }

        if (terminalControl < 1)
        {
            VAEEncodeNode refine = Assert.IsType<VAEEncodeNode>(
                terminal.LatentImage.Connection?.Node);
            Assert.True(ReachesUpstream(bridge, refine, first.Id));
            Assert.False(ReachesUpstream(bridge, refine, last.Id));
        }
        else
        {
            Assert.Same(last.Latent, terminal.LatentImage.Connection);
        }
        // The request keeps the end image; the extension consumes it per stage, not off the input.
        Assert.NotNull(generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null));
        Assert.True(ReachesUpstream(
            bridge, live.FinalVideoSave().Images.Connection?.Node, terminal.Id));

        live.AssertAllLive(endImage, last, first, terminal);
        AssertShippable(bridge, workflow, live);
    }

    // ---- timeline assembly --------------------------------------------------------------

    /// <summary>
    /// A clip's length reaches WAN's 4k+1 grid from either direction — the request's frame count is
    /// snapped down, an authored clip duration is snapped up — and the published video carries the
    /// snapped count at the timeline's dimensions.
    /// </summary>
    [Theory]
    [InlineData(16, null)]
    [InlineData(25, 0.5)]
    public async Task A_clips_length_lands_on_the_frame_grid_from_either_direction(
        int requestedFrames,
        double? clipDuration)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = clipDuration is null
            ? MakeClip(fixture.Stage(steps: 6))
            : MakeClip(clipDuration.Value, fixture.Stage(steps: 6));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(clip), post => post["videoframes"] = requestedFrames));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        WanImageToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Assert.Equal(13, conditioning.Length.LiteralAsInt());
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(13, generator.CurrentMedia.Frames);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Same(
            bridge.ResolvePath(generator.CurrentMedia.Path)?.Node,
            live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive(conditioning, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Two identical hard-cut clips share one conditioning node — same host image, same prompt, same
    /// length — and differ only in sampler seed. The timeline merge is what makes them two clips,
    /// and with intermediates on the first clip is also saved on its own.
    /// </summary>
    [Fact]
    public async Task Identical_hard_cut_clips_share_conditioning_and_differ_only_by_seed()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(
            MakeClip(fixture.Stage(steps: 8)),
            MakeClip(fixture.Stage(steps: 8)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["outputintermediateimages"] = true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        WanImageToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Same(conditioning.Positive, first.Positive.Connection);
        Assert.Same(conditioning.Positive, second.Positive.Connection);
        Assert.Same(conditioning.Latent, first.LatentImage.Connection);
        Assert.Same(conditioning.Latent, second.LatentImage.Connection);

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, first.Id));
        Assert.True(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, second.Id));
        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(2, saves.Length);
        Assert.Same(merged, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Equal(50, generator.CurrentMedia.Frames);

        live.AssertAllLive(conditioning, first, second, merged);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// Each clip's stage handoff stays inside that clip: clip 1's re-encode traces to clip 1's
    /// first sampler and not clip 0's, and the merge takes each clip's terminal stage in authored
    /// order. Both directions are asserted, since a handoff that reached everything would satisfy
    /// only the positive half.
    /// </summary>
    [Fact]
    public async Task Hard_cut_clips_keep_their_stage_handoffs_clip_local()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(
            MakeClip(
                fixture.Stage(control: 1, steps: 8),
                fixture.Stage("PreviousStage", control: 0.5, steps: 9)),
            MakeClip(
                fixture.Stage(control: 1, steps: 10),
                fixture.Stage("PreviousStage", control: 0.5, steps: 11)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstOpen = StageSampler(bridge, 0);
        SwarmKSamplerNode firstClose = StageSampler(bridge, 1);
        SwarmKSamplerNode secondOpen = StageSampler(bridge, 2);
        SwarmKSamplerNode secondClose = StageSampler(bridge, 3);
        Assert.Equal(8, firstOpen.Steps.LiteralAsInt());
        Assert.Equal(11, secondClose.Steps.LiteralAsInt());

        VAEEncodeNode firstHandoff = Assert.IsType<VAEEncodeNode>(
            firstClose.LatentImage.Connection?.Node);
        VAEEncodeNode secondHandoff = Assert.IsType<VAEEncodeNode>(
            secondClose.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, firstHandoff, firstOpen.Id));
        Assert.False(ReachesUpstream(bridge, firstHandoff, secondOpen.Id));
        Assert.True(ReachesUpstream(bridge, secondHandoff, secondOpen.Id));
        Assert.False(ReachesUpstream(bridge, secondHandoff, firstOpen.Id));
        AssertOneDecodePerStage(bridge, firstOpen, firstClose, secondOpen, secondClose);

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, firstClose.Id));
        Assert.False(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, secondClose.Id));
        Assert.True(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, secondClose.Id));
        Assert.False(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, firstClose.Id));
        Assert.Same(merged, bridge.ResolvePath(generator.CurrentMedia.Path)?.Node);

        live.AssertAllLive(firstHandoff, secondHandoff, merged);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Sampling continuations are clip-local too: each clip's low stage picks up its own clip's
    /// high latent, and only the two low stages are decoded — the shared runs never cross.
    /// </summary>
    [Fact]
    public async Task Hard_cut_sampling_continuations_stay_inside_their_clips()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateNoisePair(withBaseModel: true);
        JObject Clip(int steps) => MakeClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: steps),
            MakeStage(
                fixture.LowNoiseModel.Name, "PreviousStage", control: 0.5, steps: steps));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(Clip(8), Clip(10))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstHigh = StageSampler(bridge, 0);
        SwarmKSamplerNode firstLow = StageSampler(bridge, 1);
        SwarmKSamplerNode secondHigh = StageSampler(bridge, 2);
        SwarmKSamplerNode secondLow = StageSampler(bridge, 3);
        Assert.Same(firstHigh, firstLow.LatentImage.Connection?.Node);
        Assert.Same(secondHigh, secondLow.LatentImage.Connection?.Node);
        // Asserted on the low samplers, not the high ones: a high sampler can never reach any low
        // sampler in either clip — the wiring above makes low strictly downstream of high — so the
        // reverse pair would hold whatever the clips were wired to. A low stage picking up the
        // other clip's high latent is the failure that is actually possible here.
        Assert.False(ReachesUpstream(bridge, secondLow, firstHigh.Id));
        Assert.False(ReachesUpstream(bridge, firstLow, secondHigh.Id));
        Assert.Empty(bridge.Graph.NodesOfType<VAEEncodeNode>());

        // Only the two low stages are decoded; the third decode is core's own base image.
        SwarmKSamplerNode[] videoSamplers = [firstHigh, firstLow, secondHigh, secondLow];
        VAEDecodeNode[] decodes = [.. bridge.Graph.NodesOfType<VAEDecodeNode>()
            .Where(decode => videoSamplers.Contains(decode.Samples.Connection?.Node))];
        Assert.Equal(2, decodes.Length);
        Assert.Single(decodes, decode => decode.Samples.Connection?.Node == firstLow);
        Assert.Single(decodes, decode => decode.Samples.Connection?.Node == secondLow);

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, firstLow.Id));
        Assert.False(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, secondLow.Id));
        Assert.True(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, secondLow.Id));
        Assert.Same(merged, bridge.ResolvePath(generator.CurrentMedia.Path)?.Node);

        live.AssertAllLive(firstHigh, firstLow, secondHigh, secondLow, merged);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An upscaled clip is conformed back to the timeline resolution before the merge — batching
    /// frames of different sizes would fail at execution — and the clip that was not upscaled goes
    /// into the merge untouched.
    /// </summary>
    [Fact]
    public async Task An_upscaled_clip_is_conformed_back_before_the_timeline_merge()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(
            MakeClip(
                fixture.Stage(steps: 8),
                fixture.Stage("PreviousStage", control: 0, upscale: 2, steps: 8)),
            MakeClip(fixture.Stage(steps: 9)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ImageScaleNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 1024 && scale.Height.LiteralAsInt() == 1024);
        ImageScaleNode conform = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 512
                && ReachesUpstream(bridge, scale, upscale.Id));
        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, conform.Id));
        Assert.False(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, upscale.Id));
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);

        live.AssertAllLive(upscale, conform, merged);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The request's frame trim is a property of the finished timeline, not of each clip: one trim
    /// node either way, taking its frames off the front of the whole assembly.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_global_frame_trim_is_applied_once_over_the_finished_timeline(int clipCount)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(
            [.. Enumerable.Range(0, clipCount).Select(_ => MakeClip(fixture.Stage(steps: 8)))]);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["trimvideostartframes"] = 4));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        // One stage sampler per clip, plus core's base pass.
        Assert.Equal(clipCount + 1, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Equal(25 * clipCount - 4, generator.CurrentMedia.Frames);
        Assert.Same(trim, live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive(trim);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A trailing passthrough stage does not own the clip's end: the global end image goes to the
    /// last stage that samples, and the passthrough adds nothing after it.
    /// </summary>
    [Fact]
    public async Task A_trailing_passthrough_does_not_take_the_global_end_image()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 10),
            fixture.Stage("PreviousStage", control: 0.5, steps: 12),
            fixture.Stage("PreviousStage", control: 0, steps: 13)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["videoendimage"] = EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode terminal = StageSampler(bridge, 1);
        Assert.Single(bridge.Graph.NodesOfType<WanImageToVideoNode>());
        WanFirstLastFrameToVideoNode last = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Same(last.Positive, terminal.Positive.Connection);
        // The stage that owns the end really did receive it, in the end slot only — without this
        // the test proves only that a first/last node exists somewhere.
        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(EndImageBase64, endImage.ImageBase64.LiteralAsString());
        Assert.True(ReachesUpstream(bridge, last.EndImage.Connection?.Node, endImage.Id));
        Assert.True(ReachesUpstream(
            bridge, last.StartImage.Connection?.Node, fixture.BaseSampler(bridge).Id));
        Assert.False(ReachesUpstream(bridge, last.StartImage.Connection?.Node, endImage.Id));
        // Core's base pass plus the two generating stages; the passthrough contributes none.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            sampler => sampler.Steps.LiteralAsInt() == 13);
        Assert.True(ReachesUpstream(
            bridge, bridge.ResolvePath(generator.CurrentMedia.Path)?.Node, terminal.Id));

        live.AssertAllLive(last, terminal);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A clip's final-frame reference only claims the end-image slot once it materializes. A
    /// <c>Base</c> reference is dropped during planning and an upload with no payload is refused at
    /// runtime; either way the request's own end image is what conditions the clip.
    /// </summary>
    [Theory]
    [InlineData("Base", "WAN final-frame reference uses source 'Base'")]
    [InlineData("Upload", "Upload WAN final-frame reference is missing")]
    public async Task An_unusable_last_reference_leaves_the_global_end_image_in_place(
        string source,
        string expectedWarning)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage(control: 1, steps: 10));
        clip["refs"] = new JArray(MakeRef(source, fromEnd: true));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(clip), post => post["videoendimage"] = EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        WanFirstLastFrameToVideoNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        SwarmLoadImageB64Node endImage = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal(EndImageBase64, endImage.ImageBase64.LiteralAsString());
        Assert.True(ReachesUpstream(bridge, conditioning.EndImage.Connection?.Node, endImage.Id));
        // The end slot is all it claims: the start is still the host base image.
        Assert.True(ReachesUpstream(
            bridge, conditioning.StartImage.Connection?.Node, fixture.BaseSampler(bridge).Id));
        Assert.False(ReachesUpstream(bridge, conditioning.StartImage.Connection?.Node, endImage.Id));
        // The two arms are unusable for different reasons — planning drops the Base one, runtime
        // refuses the payload-less Upload — and each says so in its own words, once. A second
        // reference warning would mean the request's own end image was questioned too.
        Assert.Single(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("reference", StringComparison.Ordinal)
                && warning.Contains(expectedWarning, StringComparison.Ordinal));

        live.AssertAllLive(endImage, conditioning, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Text-to-video hard cuts have no host image to donate, so both 5B clips sample the one bare
    /// native latent and are told apart only by seed. Core's own video root — which the timeline
    /// replaces — is gone from the shipped graph, and no silent audio track is invented for an
    /// architecture that produces none.
    /// </summary>
    [Fact]
    public async Task Wan5b_text_hard_cuts_join_in_order_off_one_bare_native_latent()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create(
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);
        JObject document = MakeDocument(
            MakeClip(fixture.Stage(steps: 10)),
            MakeClip(fixture.Stage(steps: 11)));
        string coreRootId = null;
        WorkflowGenerator generator = null;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(document, post => post["outputintermediateimages"] = true),
            extraSteps:
            [
                new(g =>
                {
                    using WorkflowBridge core = WorkflowBridge.Create(g.Workflow);
                    coreRootId = core.ResolvePath(g.CurrentMedia.Path)?.Node.Id;
                }, Constants.WorkflowStepPriority.CoreImageToVideo + 0.01),
                new(g => generator = g, double.MaxValue),
            ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.NotNull(coreRootId);
        Assert.False(workflow.ContainsKey(coreRootId));

        Wan22ImageToVideoLatentNode latent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        // Absent, not merely unconnected: a literal here would still be a donor.
        Assert.False(latent.StartImage.HasValue);
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Same(latent, first.LatentImage.Connection?.Node);
        Assert.Same(latent, second.LatentImage.Connection?.Node);
        Assert.Empty(bridge.Graph.NodesOfType<WanImageToVideoNode>());

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, first.Id));
        Assert.False(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, second.Id));
        Assert.True(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, second.Id));
        Assert.Same(merged, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().Count);
        Assert.Equal(50, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Null(generator.CurrentMedia.AttachedAudio);
        Assert.Empty(bridge.Graph.NodesOfType<EmptyAudioNode>());

        live.AssertAllLive(latent, first, second, merged);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// Two clips in one timeline may run different WAN profiles: the 14B clip conditions through
    /// <c>WanImageToVideo</c> and the 5B clip through its own native latent, each off its own
    /// checkpoint loader, and both land in the merge.
    /// </summary>
    [Fact]
    public async Task Hard_cut_clips_may_execute_different_wan_profiles()
    {
        using MultiModelFixture fixture = MultiModelFixture.CreateWithBaseModel(
            WanWorkflowFixture.Wan22I2v14bFixturePath,
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);
        JObject document = MakeDocument(
            MakeClip(MakeStage(fixture.Model.Name, "Generated", steps: 10)),
            MakeClip(MakeStage(fixture.Models[1].Name, "Generated", steps: 11)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode fourteen = StageSampler(bridge, 0);
        SwarmKSamplerNode five = StageSampler(bridge, 1);
        WanImageToVideoNode fourteenConditioning = Assert.Single(
            bridge.Graph.NodesOfType<WanImageToVideoNode>());
        Wan22ImageToVideoLatentNode fiveLatent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        Assert.Same(fourteenConditioning.Latent, fourteen.LatentImage.Connection);
        Assert.Same(fiveLatent, five.LatentImage.Connection?.Node);
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bFixturePath),
            ModelBranchOf(fourteen).Loader.UnetName.LiteralAsString());
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22Ti2v5bFixturePath),
            ModelBranchOf(five).Loader.UnetName.LiteralAsString());

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, fourteen.Id));
        Assert.True(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, five.Id));
        Assert.Equal(50, generator.CurrentMedia.Frames);

        // Two checkpoints, two cached loader tuples, one cleanup pass over both clips.
        AssertLoaderTupleIsLive(
            workflow,
            generator.NodeHelpers[$"modelloader_{fixture.Model.Name}_image2video"],
            ModelBranchOf(fourteen).Loader);
        AssertLoaderTupleIsLive(
            workflow,
            generator.NodeHelpers[$"modelloader_{fixture.Models[1].Name}_image2video"],
            ModelBranchOf(five).Loader);

        live.AssertAllLive(fourteenConditioning, fiveLatent, fourteen, five, merged);
        AssertShippable(bridge, workflow, live);
    }

    // ---- mixed LTX-2 / WAN timelines ----------------------------------------------------

    /// <summary>
    /// An LTX-2 clip and a WAN clip in one text-to-video timeline stay on their own architectures:
    /// only the WAN clip gets a native latent, and the merge follows the authored order in both
    /// directions. WAN produces no audio, so the timeline pads its span with silence and joins it
    /// to LTX-2's real audio in the same order as the video.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mixed_text_timeline_keeps_order_provenance_and_audio_per_clip(bool wanFirst)
    {
        using MultiModelFixture fixture = MultiModelFixture.Create(
            Ltx2WorkflowFixture.ModelFixturePath,
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);
        JObject ltxClip = MakeClip(MakeStage(fixture.Model.Name, "Generated", steps: 7));
        JObject wanClip = MakeClip(MakeStage(fixture.Models[1].Name, "Generated", steps: 9));
        JObject document = wanFirst
            ? MakeDocument(wanClip, ltxClip)
            : MakeDocument(ltxClip, wanClip);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(document, post => post["outputintermediateimages"] = true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode firstSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode secondSampler = StageSampler(bridge, 1);
        SwarmKSamplerNode wanSampler = wanFirst ? firstSampler : secondSampler;
        SwarmKSamplerNode ltxSampler = wanFirst ? secondSampler : firstSampler;
        Wan22ImageToVideoLatentNode wanLatent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        Assert.False(wanLatent.StartImage.HasValue);
        Assert.Same(wanLatent, wanSampler.LatentImage.Connection?.Node);
        Assert.False(ReachesUpstream(bridge, ltxSampler, wanLatent.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, firstSampler.Id));
        Assert.False(ReachesUpstream(bridge, merged.Images[0].Connection?.Node, secondSampler.Id));
        Assert.True(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, secondSampler.Id));
        Assert.False(ReachesUpstream(bridge, merged.Images[1].Connection?.Node, firstSampler.Id));
        Assert.Same(merged, bridge.ResolvePath(generator.CurrentMedia.Path)?.Node);
        Assert.Equal(50, generator.CurrentMedia.Frames);
        Assert.Null(generator.CurrentMedia.Compat);

        EmptyAudioNode silence = Assert.Single(bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        AudioConcatNode joined = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            audio => ReachesUpstream(
                bridge,
                bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path)?.Node,
                audio.Id));
        ComfyNode wanAudio = wanFirst
            ? joined.Audio1.Connection?.Node
            : joined.Audio2.Connection?.Node;
        ComfyNode ltxAudio = wanFirst
            ? joined.Audio2.Connection?.Node
            : joined.Audio1.Connection?.Node;
        Assert.Same(silence, wanAudio);
        Assert.True(ReachesUpstream(bridge, ltxAudio, ltxSampler.Id));

        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(2, saves.Length);
        Assert.Same(merged, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Single(
            saves,
            save => save.Images.Connection?.Node != merged
                && ReachesUpstream(bridge, save.Images.Connection?.Node, firstSampler.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, secondSampler.Id));

        live.AssertAllLive(wanLatent, silence, ltxSampler, wanSampler, merged);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// A WAN source clip beside a generated LTX-2 clip: the WAN stage refines its own footage and
    /// never the host root image, the LTX-2 clip drives from the host root and never the footage,
    /// and each clip contributes its own audio span — real audio for LTX-2, silence the length of
    /// the conformed window for WAN. The request's trim lands once, on the assembled timeline and
    /// its audio alike.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mixed_source_and_generated_timeline_isolates_provenance_and_audio(
        bool wanFirst)
    {
        using MultiModelFixture fixture = MultiModelFixture.CreateWithBaseModel(
            Ltx2WorkflowFixture.ModelFixturePath,
            WanWorkflowFixture.Wan22I2v14bFixturePath);
        JObject ltxClip = MakeClip(MakeStage(fixture.Model.Name, "Generated", steps: 7));
        JObject wanClip = SourceClip(
            MakeStage(fixture.Models[1].Name, "Generated", control: 0.5, steps: 9));
        JObject document = wanFirst
            ? MakeDocument(wanClip, ltxClip)
            : MakeDocument(ltxClip, wanClip);
        string rootImageId = null;
        WorkflowGenerator generator = null;

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.ImageToVideoPost(document, post =>
            {
                post["outputintermediateimages"] = true;
                post["trimvideostartframes"] = 4;
            }),
            extraSteps:
            [
                new(g =>
                {
                    using WorkflowBridge root = WorkflowBridge.Create(g.Workflow);
                    rootImageId = root.ResolvePath(g.CurrentMedia.Path)?.Node.Id;
                }, Constants.WorkflowStepPriority.CapturePreCoreVideoMedia - 0.1),
                new(g => generator = g, double.MaxValue),
            ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.NotNull(rootImageId);
        SwarmFrameWindowNode window = AssertSourceConformChain(bridge);
        SwarmKSamplerNode wanSampler = StageSampler(bridge, wanFirst ? 0 : 1);
        SwarmKSamplerNode ltxSampler = StageSampler(bridge, wanFirst ? 1 : 0);
        VAEEncodeNode wanLatent = Assert.IsType<VAEEncodeNode>(
            wanSampler.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, wanLatent, window.Id));
        Assert.False(ReachesUpstream(bridge, wanLatent, rootImageId));
        Assert.True(ReachesUpstream(bridge, ltxSampler, rootImageId));
        Assert.False(ReachesUpstream(bridge, ltxSampler, window.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Same(merged, trim.Image.Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        // 25 generated LTX frames plus the 17-frame conformed WAN window, less the trim.
        Assert.Equal(38, generator.CurrentMedia.Frames);
        Assert.Same(trim, bridge.ResolvePath(generator.CurrentMedia.Path)?.Node);

        EmptyAudioNode silence = Assert.Single(bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(SourceClipFrames / 24.0, silence.Duration.LiteralAsDouble()!.Value, 6);
        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path)?.Node);
        Assert.Equal(4 / 24.0, audioTrim.StartIndex.LiteralAsDouble()!.Value, 6);
        Assert.Equal(38 / 24.0, audioTrim.Duration.LiteralAsDouble()!.Value, 6);
        AudioConcatNode joined = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            concat => ReachesUpstream(bridge, audioTrim, concat.Id));
        Assert.Same(
            silence,
            wanFirst ? joined.Audio1.Connection?.Node : joined.Audio2.Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge,
            wanFirst ? joined.Audio2.Connection?.Node : joined.Audio1.Connection?.Node,
            ltxSampler.Id));

        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().Count);
        Assert.Same(trim, live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive(window, wanLatent, ltxSampler, wanSampler, trim, silence);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// A multi-stage WAN clip after an LTX-2 clip keeps its internal stage chain separate from the
    /// timeline's: the WAN refine hands off from the WAN opener and not from the LTX-2 clip, and
    /// each clip's intermediates are its own.
    /// </summary>
    [Fact]
    public async Task A_multi_stage_wan_clip_after_an_ltx_clip_keeps_its_chain_to_itself()
    {
        using MultiModelFixture fixture = MultiModelFixture.CreateWithBaseModel(
            Ltx2WorkflowFixture.ModelFixturePath,
            WanWorkflowFixture.Wan22I2v14bFixturePath);
        JObject document = MakeDocument(
            MakeClip(MakeStage(fixture.Model.Name, "Generated", steps: 7)),
            MakeClip(
                MakeStage(fixture.Models[1].Name, "Generated", steps: 9),
                MakeStage(
                    fixture.Models[1].Name, "PreviousStage", control: 0.8, steps: 10)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["outputintermediateimages"] = true;
                    post["trimvideostartframes"] = 4;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode ltx = StageSampler(bridge, 0);
        SwarmKSamplerNode wanOpen = StageSampler(bridge, 1);
        SwarmKSamplerNode wanClose = StageSampler(bridge, 2);
        // floor(10 * (1 - 0.8)) is 1, not 2: 1 - 0.8 lands just under 0.2 in binary.
        Assert.Equal(1, wanClose.StartAtStep.LiteralAsInt());
        WanImageToVideoNode wanConditioning = Assert.IsType<WanImageToVideoNode>(
            wanOpen.Positive.Connection?.Node);
        Assert.False(ReachesUpstream(
            bridge, wanConditioning.StartImage.Connection?.Node, ltx.Id));
        VAEEncodeNode handoff = Assert.IsType<VAEEncodeNode>(
            wanClose.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, handoff, wanOpen.Id));
        Assert.False(ReachesUpstream(bridge, handoff, ltx.Id));

        BatchImagesNodeNode merged = Assert.Single(
            bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Same(merged, trim.Image.Connection?.Node);
        Assert.Equal(46, generator.CurrentMedia.Frames);
        Assert.True(ReachesUpstream(bridge, trim, wanClose.Id));

        EmptyAudioNode silence = Assert.Single(bridge.Graph.NodesOfType<EmptyAudioNode>());
        Assert.Equal(25 / 24.0, silence.Duration.LiteralAsDouble()!.Value, 6);
        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path)?.Node);
        Assert.Equal(46 / 24.0, audioTrim.Duration.LiteralAsDouble()!.Value, 6);
        AudioConcatNode joined = Assert.Single(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            concat => ReachesUpstream(bridge, audioTrim, concat.Id));
        Assert.True(ReachesUpstream(bridge, joined.Audio2.Connection?.Node, silence.Id));
        Assert.False(ReachesUpstream(bridge, joined.Audio1.Connection?.Node, silence.Id));

        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(3, saves.Length);
        Assert.Same(trim, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Contains(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, ltx.Id)
                && save.Images.Connection?.Node != trim);
        Assert.Contains(
            saves,
            save => ReachesUpstream(bridge, save.Images.Connection?.Node, wanOpen.Id)
                && !ReachesUpstream(bridge, save.Images.Connection?.Node, wanClose.Id));

        live.AssertAllLive(handoff, ltx, wanOpen, wanClose, trim, silence);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }

    /// <summary>
    /// <c>donotsave</c> strips every output node — the state
    /// <see cref="WorkflowLivePath.AssertNoOrphanNodes"/> and <c>FinalVideoSave</c> cannot describe,
    /// since with nothing to reach "orphaned" has no meaning. Pinned as today's behaviour, not as
    /// intent: ComfyUI rejects an output-less prompt with <c>prompt_no_outputs</c> (P5 in
    /// <c>nonversioned/20260804-production-findings.md</c>). What must still hold is that nothing
    /// dangles behind the removal.
    /// <para>
    /// Text-to-video is the only shape where this can be asserted at all: core saves the
    /// image-to-video base pass regardless of <c>donotsave</c>, so an image-to-video request always
    /// ships one output node the extension does not own.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_do_not_save_mixed_timeline_publishes_nothing_and_leaves_no_dangling_refs()
    {
        using MultiModelFixture fixture = MultiModelFixture.Create(
            Ltx2WorkflowFixture.ModelFixturePath,
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);
        JObject document = MakeDocument(
            MakeClip(MakeStage(fixture.Model.Name, "Generated", steps: 7)),
            MakeClip(MakeStage(fixture.Models[1].Name, "Generated", steps: 9)));

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(fixture.Post(
            document,
            post =>
            {
                post["outputintermediateimages"] = true;
                post["donotsave"] = true;
            }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        live.AssertNoPublishedOutput();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// A later stage's Control is a fraction of its own step count, and 0.9 over 8 steps floors to
    /// start step 0 — a refining pass that would silently regenerate everything. The request is
    /// refused readably, before any VideoStages phase touches the graph.
    /// <para>
    /// Neither stage authors an image reference, so this also pins the defaults that make stage 1 a
    /// refining pass at all: <c>Generated</c> first, <c>PreviousStage</c> after.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_later_control_that_quantizes_to_start_step_zero_is_refused()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject first = fixture.Stage(steps: 8);
        JObject second = fixture.Stage(control: 0.9, steps: 8);
        first.Remove("imageReference");
        second.Remove("imageReference");
        WorkflowGenerator captured = null;
        JObject beforePreflight = null;
        VideoStagesSpec parsed = null;

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(MakeDocument(MakeClip(first, second))),
                extraSteps:
                [
                    new(g =>
                    {
                        captured = g;
                        beforePreflight = (JObject)g.Workflow.DeepClone();
                        parsed = g.GetVideoStagesSpec();
                    }, Constants.WorkflowStepPriority.PreflightRequest - 0.1),
                ]));

        Assert.Contains("quantizes to sampler start step 0", error.Message);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);
        Assert.True(
            JToken.DeepEquals(beforePreflight, captured.Workflow),
            "A VideoStages phase mutated the graph before preflight refused the request.");
    }

    // ---- request-global settings the timeline refuses -----------------------------------

    /// <summary>
    /// The request's global end image only has a home when exactly one clip ends the timeline on a
    /// profile that takes an end frame. Every other shape warns and drops it rather than guessing:
    /// more than one clip has no single last frame; the 5B profile has no first/last conditioning
    /// node at all; a text-to-video entry has no conditioning to attach it to; and a source clip's
    /// ending is the footage's.
    /// <para>
    /// The end image is left on the request in every case — it is ignored, not consumed — and
    /// <see cref="The_global_end_image_belongs_to_the_clips_terminal_generating_stage"/> is the
    /// control that it does reach the graph when it can.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, "two-clips")]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, "image-to-video")]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, "text-to-video")]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, "text-to-video")]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, "source-clip")]
    public async Task An_unusable_global_end_image_warns_and_is_dropped(
        string modelFixturePath,
        string shape)
    {
        bool textToVideo = shape == "text-to-video";
        using WanWorkflowFixture fixture = textToVideo
            ? WanWorkflowFixture.Create(modelFixturePath)
            : WanWorkflowFixture.CreateWithBaseModel(modelFixturePath);
        JObject document = shape switch
        {
            "two-clips" => MakeDocument(
                MakeClip(fixture.Stage(steps: 10)), MakeClip(fixture.Stage(steps: 10))),
            "source-clip" => MakeDocument(SourceClip(fixture.Stage(control: 1, steps: 10))),
            _ => MakeDocument(MakeClip(fixture.Stage(control: 1, steps: 10))),
        };
        void Customize(JObject post) => post["videoendimage"] = EndImagePayload;
        Image endFrameBeforeStages = null;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                textToVideo
                    ? fixture.Post(document, Customize)
                    : fixture.ImageToVideoPost(document, Customize),
                extraSteps:
                [
                    new(g => endFrameBeforeStages =
                            g.UserInput.Get(T2IParamTypes.VideoEndFrame, null),
                        Constants.WorkflowStepPriority.RunConfiguredStages - 0.01),
                ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("'Video End Frame' was ignored", StringComparison.Ordinal));
        Assert.Empty(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        // The same instance, not merely some image: "ignored" means untouched, and a path that
        // swapped in a replacement would satisfy a non-null check.
        Assert.NotNull(endFrameBeforeStages);
        Assert.Same(
            endFrameBeforeStages,
            generator.UserInput.Get(T2IParamTypes.VideoEndFrame, null));

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A timeline whose clips are on different architectures has no single WAN clip to end, so the
    /// end image is dropped and the warning names both families — the arm that separates this from
    /// the plain two-clip refusal, where every clip is WAN.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mixed_timeline_drops_the_global_end_image_and_names_both_families(
        bool wanFirst)
    {
        using MultiModelFixture fixture = MultiModelFixture.Create(
            Ltx2WorkflowFixture.ModelFixturePath,
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);
        JObject ltxClip = MakeClip(MakeStage(fixture.Model.Name, "Generated", steps: 7));
        JObject wanClip = MakeClip(MakeStage(fixture.Models[1].Name, "Generated", steps: 9));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    wanFirst
                        ? MakeDocument(wanClip, ltxClip)
                        : MakeDocument(ltxClip, wanClip),
                    post => post["videoendimage"] = EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        string families = wanFirst ? "wan22, ltx2" : "ltx2, wan22";
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("'Video End Frame' was ignored", StringComparison.Ordinal)
                && warning.Contains($"architecture(s): {families}", StringComparison.Ordinal));
        Assert.Empty(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());

        // Both clips still generated; only the end image was refused.
        live.AssertAllLive(StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// SwarmUI's request-global Video2Video Creativity would start the diffusion late; WAN stages
    /// carry their own <c>Control</c>, so the global value is warned about and ignored — the stage
    /// starts at step 0 as its own <c>control: 1</c> asks, not at the step 0.5 creativity implies.
    /// </summary>
    [Fact]
    public async Task A_global_creativity_warns_and_the_stages_own_control_decides()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(fixture.Stage(control: 1, steps: 10))),
                    post => post["video2videocreativity"] = 0.5));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(10, stage.Steps.LiteralAsInt());
        Assert.Equal(0, stage.StartAtStep.LiteralAsInt());
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(
                "refinement strength is clip-local", StringComparison.Ordinal));

        live.AssertAllLive(stage);
        AssertShippable(bridge, workflow, live);
    }
}
