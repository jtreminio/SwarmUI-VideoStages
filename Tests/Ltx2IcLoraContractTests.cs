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
/// LTX-2 IC-LoRA: which stages a loader patches, where a guide's drive frames come from, how the
/// control-signal chains splice in, and how an audio-consuming preset reaches the conditioning —
/// all generated through the real Comfy API POST path.
/// <para>
/// None of the IC-LoRA node types are in core's <c>AutoCleanupNodeTypes</c>, so an IC-LoRA subgraph
/// that ends up orphaned is shipped to Comfy as-is. Every test here closes with
/// <see cref="AssertShippable"/>, which is what catches that.
/// </para>
/// <para>
/// Shape: text-to-video unless a test needs the base image, either as IC-LoRA entry media or —
/// for the clip-audio test — to avoid the clip-0 audio-conditioning defect the findings doc calls
/// P1.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class Ltx2IcLoraContractTests
{
    private const string DriveVideo = "data:video/mp4;base64,QUJD";
    private const string OtherDriveVideo = "data:video/mp4;base64,REVG";
    private const string DriveImage = "data:image/png;base64,QUJD";
    private const string DriveAudio = "data:audio/wav;base64,QUJD";

    private static JObject MakeIcLora(
        string lora,
        string source = Constants.IcLoraSourceUpload,
        double strength = 1.0,
        double attentionStrength = 1.0,
        string controlType = Constants.IcLoraControlNone,
        string driveMediaData = null,
        string driveMediaFileName = "drive.mp4",
        IcLoraDriveData? driveData = null)
    {
        JObject entry = new()
        {
            ["lora"] = lora,
            ["driveSource"] = source,
            ["driveData"] = $"{driveData ?? (driveMediaData is null
                ? IcLoraDriveData.None
                : IcLoraDriveData.Visual)}",
            ["strength"] = strength,
            ["attentionStrength"] = attentionStrength,
            ["controlType"] = controlType,
        };
        if (driveMediaData is not null)
        {
            entry["driveMedia"] = new JObject
            {
                ["data"] = driveMediaData,
                ["fileName"] = driveMediaFileName,
            };
        }
        return entry;
    }

    private static JObject IcLoraClip(JObject[] stages, params JObject[] entries)
    {
        JObject clip = MakeClip(stages);
        clip["icLoras"] = new JArray(entries);
        return clip;
    }

    /// <summary>A dropped entry leaves nothing of itself behind.</summary>
    private static void AssertNoIcLoraNodes(WorkflowBridge bridge)
    {
        Assert.Empty(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());
    }

    private static LTXAddVideoICLoRAGuideNode OnlyGuide(WorkflowBridge bridge) =>
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

    /// <summary>True when the guide's drive frames trace back to <paramref name="wanted"/>.</summary>
    private static bool GuideDrivenBy(
        WorkflowBridge bridge,
        LTXAddVideoICLoRAGuideNode guide,
        ComfyNode wanted) =>
        ReachesUpstream(bridge, guide.Image.Connection?.Node, wanted.Id);

    /// <summary>
    /// The framing node the guide's drive frames pass through, walking back over the trim/repeat
    /// wrappers the guide builder puts in front of it.
    /// </summary>
    private static ComfyNode GuideFraming(LTXAddVideoICLoRAGuideNode guide)
    {
        ComfyNode current = guide.Image.Connection?.Node;
        HashSet<string> visited = [];
        while (current is not null && visited.Add(current.Id))
        {
            if (current is ImageScaleNode or SwarmFrameImageNode)
            {
                return current;
            }
            current = current.FindInput("image")?.Connection?.Node
                ?? current.FindInput("images")?.Connection?.Node;
        }
        return null;
    }

    /// <summary>
    /// The auto-model token resolves a preset to weights already on disk. The dotted file name is
    /// the legacy spelling core's strict filename clean would otherwise mangle, so it is pinned.
    /// </summary>
    [Fact]
    public async Task Auto_ic_lora_resolves_the_presets_downloaded_weights()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9.safetensors");

        JObject entry = MakeIcLora(IcLoraWeights.AutoModelToken, driveMediaData: DriveVideo);
        entry["preset"] = "deblur";

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip([fixture.Stage()], entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal(
            "LTX-2/IC-LoRA/ltx-2.3-22b-ic-lora-deblur-0.9.safetensors",
            loader.LoraName.LiteralAsString());

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(loader, sampler.Model.Connection?.Node);
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        live.AssertAllLive(loader, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A stage-scoped entry patches one stage. Counting loaders cannot show which one, so both
    /// samplers are checked: the scoped stage reaches the loader and its guide, the other reaches
    /// neither.
    /// </summary>
    [Fact]
    public async Task Stage_scoped_entry_applies_only_on_its_target_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo);
        entry["stage"] = 1;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);

        SwarmKSamplerNode unscoped = StageSampler(bridge, 0);
        SwarmKSamplerNode scoped = StageSampler(bridge, 1);
        Assert.False(ReachesUpstream(bridge, unscoped, loader.Id));
        Assert.False(ReachesUpstream(bridge, unscoped, guide.Id));
        Assert.Same(loader, scoped.Model.Connection?.Node);
        // Reachability would be inherited through stage 0's latent, so the direct wiring is the
        // claim: the guide is what conditions this stage.
        Assert.Same(guide, scoped.Positive.Connection?.Node);

        live.AssertAllLive(loader, guide, unscoped, scoped);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Runtime stage ids are flat across clips; an entry's <c>stage</c> is clip-relative. Stage 0
    /// of the second clip is runtime stage 1, and that — not runtime stage 0 — is what must be
    /// patched.
    /// </summary>
    [Fact]
    public async Task Stage_scope_is_clip_relative_not_global()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo);
        entry["stage"] = 0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(
            MakeClip(fixture.Stage()),
            IcLoraClip([fixture.Stage()], entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        SwarmKSamplerNode firstClip = StageSampler(bridge, 0);
        SwarmKSamplerNode secondClip = StageSampler(bridge, 1);
        Assert.False(ReachesUpstream(bridge, firstClip, loader.Id));
        Assert.Same(loader, secondClip.Model.Connection?.Node);

        live.AssertAllLive(loader, firstClip, secondClip);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An unscoped entry patches every stage. They all ask for the same patch, so they sample
    /// through one loader; what stays per stage is the guide each carries.
    /// </summary>
    [Fact]
    public async Task Unscoped_entry_applies_on_every_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>().Count);

        SwarmKSamplerNode[] stages = [StageSampler(bridge, 0), StageSampler(bridge, 1)];
        // Both stages patched, and by the same node: the entry is unscoped, so the two stages ask
        // for an identical patch and there is only one to make. Each still guides for itself.
        Assert.All(stages, stage => Assert.Same(loader, stage.Model.Connection?.Node));
        Assert.Equal(
            2,
            stages.Select(stage => stage.Positive.Connection?.Node?.Id).Distinct().Count());

        live.AssertAllLive([loader, .. stages]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The <c>Incoming</c> drive source takes whatever media enters the stage. That is the host
    /// base image on stage 0 and the previous stage's output on stage 1, so asserting only "a guide
    /// exists" would pass with the guide fed by entirely the wrong media.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Stage_input_source_drives_guide_from_the_stages_input_frames(int stageId)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraA",
            source: Constants.IcLoraSourceIncoming,
            driveData: IcLoraDriveData.Visual);
        entry["stage"] = stageId;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(control: 0.5), fixture.Stage("PreviousStage", control: 0.5)],
            entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Nothing is uploaded: the drive frames are the stage's own input.
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        SwarmKSamplerNode scoped = StageSampler(bridge, stageId);
        Assert.True(ReachesUpstream(bridge, scoped, guide.Id));

        ImageScaleNode resize = Assert.IsType<ImageScaleNode>(GuideFraming(guide));
        // Neither stage upscales, so these match the POST's own 512x512 and cannot tell a stage
        // resolution from an inherited request resolution — see
        // Uploaded_drive_media_is_resized_to_stage_dimensions for the discriminating shape.
        Assert.Equal(VideoStagesWorkflowFixture.Width, resize.Width.LiteralAsInt());
        Assert.Equal(VideoStagesWorkflowFixture.Height, resize.Height.LiteralAsInt());
        Assert.Equal("center", resize.Crop.LiteralAsString());

        // Both arms trace to the base image; the discriminator is whether the frames have been
        // through stage 0's sampler on the way.
        Assert.True(GuideDrivenBy(bridge, guide, BaseImage(bridge)));
        Assert.Equal(stageId == 1, GuideDrivenBy(bridge, guide, StageSampler(bridge, 0)));

        live.AssertAllLive(guide, resize, scoped);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// On a latent-space upscale the incoming media is still a latent, so the guide gets a decode
    /// of its own. That decode must stay off the sampling path — the upsampler keeps consuming the
    /// raw latent — or the stage would re-encode pixels it never needed to leave.
    /// </summary>
    [Fact]
    public async Task Stage_input_source_works_on_a_latent_upscale_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraA",
            source: Constants.IcLoraSourceIncoming,
            driveData: IcLoraDriveData.Visual);
        entry["stage"] = 1;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [
                fixture.Stage(),
                fixture.Stage(upscale: 2.0, upscaleMethod: "latent-bislerp"),
            ],
            entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);

        // The upsampler continues in latent space, straight off the previous stage's split.
        LatentUpscaleByNode scaler = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Same(OutputOf(bridge, first).VideoLatent, scaler.Samples.Connection);
        Assert.True(ReachesUpstream(bridge, second.LatentImage.Connection?.Node, scaler.Id));

        // The guide's decode hangs off that same latent and feeds nothing but the guide.
        VAEDecodeTiledNode decode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeTiledNode>(),
            node => GuideDrivenBy(bridge, guide, node));
        Assert.Same(OutputOf(bridge, first).VideoLatent, decode.Samples.Connection);
        Assert.False(ReachesUpstream(bridge, scaler, decode.Id));

        live.AssertAllLive(guide, decode, scaler, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Uploaded drive media is framed to the resolution its own stage generates at, which a pixel
    /// upscale moves away from the request's. The dimensions are the whole claim — a resize to the
    /// wrong size still looks like a resize.
    /// </summary>
    [Fact]
    public async Task Uploaded_drive_media_is_resized_to_stage_dimensions()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo);
        entry["stage"] = 1;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage(upscale: 2.0)],
            entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        ImageScaleNode resize = Assert.IsType<ImageScaleNode>(GuideFraming(guide));
        // 2× the POST's 512, so neither edge can be right by inheriting the request.
        Assert.Equal(1024, resize.Width.LiteralAsInt());
        Assert.Equal(1024, resize.Height.LiteralAsInt());
        Assert.Equal("center", resize.Crop.LiteralAsString());

        live.AssertAllLive(guide, resize, StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A non-crop clip framing mode routes the same drive media through the pad-and-fit node
    /// instead of a plain scale.
    /// </summary>
    [Fact]
    public async Task Uploaded_drive_media_uses_the_clips_green_fit_method()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject clip = IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        // Non-square: SwarmFrameImage's own default is 512x512, the fixture's resolution.
        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip), post =>
        {
            post["width"] = 768;
            post["height"] = 448;
        });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        SwarmFrameImageNode frame = Assert.IsType<SwarmFrameImageNode>(GuideFraming(guide));
        Assert.Equal(Constants.ReferenceFramingFitGreen, frame.Method.LiteralAsString());
        Assert.Equal(768, frame.Width.LiteralAsInt());
        Assert.Equal(448, frame.Height.LiteralAsInt());
        Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());

        live.AssertAllLive(guide, frame, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Two entries on one stage chain: the loaders patch one model in sequence and the guides thread
    /// conditioning and latent through each other, each reading its own loader's downscale factor.
    /// The stage's controlNetStrength is what both guides sample at.
    /// </summary>
    [Fact]
    public async Task Two_uploaded_ic_loras_chain_loaders_and_stack_guides()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors")
            .InstallModel("LoRA", "UnitTest_IcLoraB.safetensors");

        JObject stage = fixture.Stage();
        stage["controlNetStrength"] = 0.7;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [stage],
            MakeIcLora("UnitTest_IcLoraA", strength: 1.2, driveMediaData: DriveVideo),
            MakeIcLora("UnitTest_IcLoraB", strength: 0.9, driveMediaData: OtherDriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        IReadOnlyList<LTXICLoRALoaderModelOnlyNode> loaders =
            [.. bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>()];
        Assert.Equal(2, loaders.Count);
        LTXICLoRALoaderModelOnlyNode first = Assert.Single(
            loaders,
            loader => loader.LoraName.LiteralAsString() == "UnitTest_IcLoraA.safetensors");
        LTXICLoRALoaderModelOnlyNode second = Assert.Single(
            loaders,
            loader => loader.LoraName.LiteralAsString() == "UnitTest_IcLoraB.safetensors");
        Assert.Equal(1.2, first.StrengthModel.LiteralAsDouble());
        Assert.Equal(0.9, second.StrengthModel.LiteralAsDouble());
        Assert.Same(first, second.ModelInput.Connection?.Node);
        Assert.Same(second, StageSampler(bridge, 0).Model.Connection?.Node);

        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides =
            [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()];
        Assert.Equal(2, guides.Count);
        LTXAddVideoICLoRAGuideNode firstGuide = Assert.Single(
            guides,
            guide => ReferenceEquals(guide.LatentDownscaleFactor.Connection?.Node, first));
        LTXAddVideoICLoRAGuideNode secondGuide = Assert.Single(
            guides,
            guide => ReferenceEquals(guide.LatentDownscaleFactor.Connection?.Node, second));
        Assert.Same(firstGuide, secondGuide.PositiveInput.Connection?.Node);
        Assert.Same(firstGuide, secondGuide.NegativeInput.Connection?.Node);
        Assert.Same(firstGuide, secondGuide.LatentInput.Connection?.Node);
        // Slot 1 of the loader is the downscale factor; slot 0 is the patched model.
        Assert.Equal(1, firstGuide.LatentDownscaleFactor.Connection?.SlotIndex);
        Assert.Equal(0.7, firstGuide.Strength.LiteralAsDouble());
        Assert.Equal(0.7, secondGuide.Strength.LiteralAsDouble());

        live.AssertAllLive([.. loaders, .. guides]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An uploaded video drive loses its data-URI prefix, is split into components and its frames
    /// reach the guide.
    /// </summary>
    [Fact]
    public async Task Uploaded_drive_video_loads_b64_with_stripped_prefix_and_feeds_guide()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadVideoB64Node load = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Equal("QUJD", load.VideoBase64.LiteralAsString());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(load, components.Video.Connection?.Node);

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        Assert.True(GuideDrivenBy(bridge, guide, components));

        live.AssertAllLive(load, components, guide);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Attention strength below 1 is the only thing that selects the advanced guide node, and it
    /// still stacks on the basic one built for the sibling entry.
    /// </summary>
    [Fact]
    public async Task Attention_strength_below_one_selects_advanced_guide()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors")
            .InstallModel("LoRA", "UnitTest_IcLoraB.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo),
            MakeIcLora(
                "UnitTest_IcLoraB",
                attentionStrength: 0.65,
                driveMediaData: OtherDriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXAddVideoICLoRAGuideNode basic = OnlyGuide(bridge);
        LTXAddVideoICLoRAGuideAdvancedNode advanced = Assert.Single(
            bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());
        Assert.Equal(0.65, advanced.AttentionStrength.LiteralAsDouble());
        Assert.Same(basic, advanced.PositiveInput.Connection?.Node);
        Assert.Same(basic, advanced.LatentInput.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, StageSampler(bridge, 0), advanced.Id));

        live.AssertAllLive(basic, advanced);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Canny_control_type_splices_canny_between_drive_video_and_guide()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlCanny,
                driveMediaData: DriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        CannyNode canny = Assert.Single(bridge.Graph.NodesOfType<CannyNode>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(components, canny.Image.Connection?.Node);

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        Assert.True(GuideDrivenBy(bridge, guide, canny));

        live.AssertAllLive(canny, guide);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Depth_control_type_splices_da3_chain()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlDepth,
                driveMediaData: DriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LoadDA3ModelNode da3Model = Assert.Single(bridge.Graph.NodesOfType<LoadDA3ModelNode>());
        Assert.Equal("depth_anything_3_mono_large.safetensors", da3Model.ModelName.LiteralAsString());
        DA3InferenceNode inference = Assert.Single(bridge.Graph.NodesOfType<DA3InferenceNode>());
        Assert.Equal("mono", inference.Mode.LiteralAsString());
        Assert.Same(da3Model, inference.Da3Model.Connection?.Node);
        Assert.Same(
            Assert.Single(bridge.Graph.NodesOfType<GetVideoComponentsNode>()),
            inference.Image.Connection?.Node);

        DA3RenderNode render = Assert.Single(bridge.Graph.NodesOfType<DA3RenderNode>());
        Assert.Equal("depth", render.Output.LiteralAsString());
        Assert.Equal("v2_style", $"{render.ExtraInputs?["output.normalization"]}");
        Assert.Same(inference, render.Da3Geometry.Connection?.Node);

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        Assert.True(GuideDrivenBy(bridge, guide, render));

        live.AssertAllLive(da3Model, inference, render, guide);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Normal_control_type_splices_moge_chain()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlNormal,
                driveMediaData: DriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LoadMoGeModelNode mogeModel = Assert.Single(bridge.Graph.NodesOfType<LoadMoGeModelNode>());
        Assert.Equal("moge_2_vitl_normal_fp16.safetensors", mogeModel.ModelName.LiteralAsString());
        MoGeInferenceNode inference = Assert.Single(bridge.Graph.NodesOfType<MoGeInferenceNode>());
        Assert.Same(mogeModel, inference.MogeModel.Connection?.Node);
        Assert.Same(
            Assert.Single(bridge.Graph.NodesOfType<GetVideoComponentsNode>()),
            inference.Image.Connection?.Node);

        MoGeRenderNode render = Assert.Single(bridge.Graph.NodesOfType<MoGeRenderNode>());
        Assert.Equal("normal_opengl", render.Output.LiteralAsString());
        Assert.Same(inference, render.MogeGeometry.Connection?.Node);

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        Assert.True(GuideDrivenBy(bridge, guide, render));

        live.AssertAllLive(mogeModel, inference, render, guide);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>An uploaded still takes the image loader, never the video one.</summary>
    [Fact]
    public async Task Uploaded_image_drive_uses_image_b64_load()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveImage))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node load = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Equal("QUJD", load.ImageBase64.LiteralAsString());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        Assert.True(GuideDrivenBy(bridge, guide, load));

        live.AssertAllLive(load, guide);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// With no drive media the entry is still a model patch. A loader with no guide has nothing but
    /// the stage's model downstream of it, so a regressed model assignment leaves it orphaned —
    /// which is what the live-path assertion catches and a node count never would.
    /// </summary>
    [Fact]
    public async Task Entry_without_drive_video_is_loader_only()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA"))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(loader, sampler.Model.Connection?.Node);

        live.AssertAllLive(loader, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An entry naming weights that are not installed is dropped, and the drop must not take the
    /// following entry's drive media with it.
    /// </summary>
    [Fact]
    public async Task Unresolvable_entry_is_skipped_and_later_entries_still_apply()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraB.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_MissingLora", driveMediaData: DriveVideo),
            MakeIcLora("UnitTest_IcLoraB", driveMediaData: OtherDriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal("UnitTest_IcLoraB.safetensors", loader.LoraName.LiteralAsString());
        // The surviving guide reads the surviving entry's upload, not the dropped one's.
        SwarmLoadVideoB64Node load = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Equal("REVG", load.VideoBase64.LiteralAsString());
        Assert.True(GuideDrivenBy(bridge, OnlyGuide(bridge), load));

        live.AssertAllLive(loader, load, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// One upload feeds every stage the entry applies on: the load/components pair is built once
    /// and both stages' guides read it, while the guides stay per stage.
    /// </summary>
    [Fact]
    public async Task Uploaded_drive_chain_is_shared_across_stages()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        // Both stages ask for the same patch, so there is one loader to make; the guides are what
        // stay per stage, and they are what the drive has to reach.
        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());

        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides =
            [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()];
        Assert.Equal(2, guides.Count);
        Assert.All(guides, guide => Assert.True(GuideDrivenBy(bridge, guide, components)));

        live.AssertAllLive([.. guides, components]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A still drive is repeated up to the clip's frame count — trimming a one-frame batch could
    /// never produce a full-length guide, and the repeat amount is the whole claim.
    /// </summary>
    [Fact]
    public async Task Still_image_drive_is_repeated_to_the_clip_frame_count()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveImage))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        RepeatImageBatchNode repeat = Assert.Single(
            bridge.Graph.NodesOfType<RepeatImageBatchNode>());
        // The POST's 25 frames are already on LTX-2's 8k+1 grid.
        Assert.Equal(25, repeat.Amount.LiteralAsInt());
        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        Assert.Same(repeat, guide.Image.Connection?.Node);

        live.AssertAllLive(repeat, guide);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A continue boundary makes the clip generate a handle ahead of its own content, so an
    /// authored drive has to be padded by the same amount to stay in sync — its first frame is held
    /// for the handle. Media that already arrives from the previous stage is not padded, because it
    /// carries the handle already.
    /// </summary>
    [Fact]
    public async Task Continue_handle_offsets_authored_drives_but_not_later_stage_output()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors")
            .InstallModel("LoRA", "UnitTest_IcLoraB.safetensors");

        JObject lead = MakeClip(0.6, fixture.Stage());
        lead["boundaryOut"] = Constants.BoundaryOutContinue;

        JObject uploaded = MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo);
        uploaded["stage"] = 0;
        JObject incoming = MakeIcLora(
            "UnitTest_IcLoraB",
            source: Constants.IcLoraSourceIncoming,
            driveData: IcLoraDriveData.Visual);
        incoming["stage"] = 1;
        JObject target = IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            uploaded,
            incoming);
        target["duration"] = 0.6;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(lead, target));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // 0.6s at 24 fps is 16 structural frames, which LTX's 8k+1 grid rounds up to 17; the
        // 8-frame continue handle the clip generates ahead of its content brings the guide to 25.
        LTXAddVideoICLoRAGuideNode[] guides =
            [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()];
        Assert.Equal(2, guides.Length);
        ImageFromBatchNode[] guideTrims = [.. guides.Select(
            guide => Assert.IsType<ImageFromBatchNode>(guide.Image.Connection?.Node))];
        Assert.All(guideTrims, trim => Assert.Equal(25, trim.Length.LiteralAsInt()));

        RepeatImageBatchNode handle = Assert.Single(
            bridge.Graph.NodesOfType<RepeatImageBatchNode>());
        Assert.Equal(8, handle.Amount.LiteralAsInt());
        ImageFromBatchNode firstFrame = Assert.IsType<ImageFromBatchNode>(
            handle.Image.Connection?.Node);
        // Both values are ImageFromBatch's codegen defaults and this wrap is extension-built, so the
        // shipped JSON carries them either way and a handle that held the wrong slice cannot be
        // authored into existence here. Ltx2BoundaryContractTests' tail slices are the control that
        // batch_index/length reach the node at all; what this pins is that the handle repeats one
        // frame rather than the whole guide.
        Assert.Equal(0, firstFrame.BatchIndex.LiteralAsInt());
        Assert.Equal(1, firstFrame.Length.LiteralAsInt());

        // Which trim belongs to which entry: a guide names its entry through the loader wired to
        // its downscale factor. Counting padded trims would never say that.
        ImageFromBatchNode TrimOfEntry(string loraFile) => Assert.IsType<ImageFromBatchNode>(
            Assert.Single(
                guides,
                guide => guide.LatentDownscaleFactor.Connection?.Node
                    is LTXICLoRALoaderModelOnlyNode loader
                    && loader.LoraName.LiteralAsString() == loraFile)
                .Image.Connection?.Node);

        // The uploaded drive is padded: the held first frame, then the upload's own frames.
        BatchImagesNodeNode padded = Assert.IsType<BatchImagesNodeNode>(
            TrimOfEntry("UnitTest_IcLoraA.safetensors").Image.Connection?.Node);
        Assert.Equal(2, padded.Images.Count);
        Assert.Same(handle, padded.Images[0].Connection?.Node);
        SwarmLoadVideoB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.True(ReachesUpstream(bridge, padded.Images[1].Connection?.Node, upload.Id));

        // The stage-1 entry reads the previous stage's output, which already carries the handle, so
        // it is trimmed only. Its media is downstream of that stage's sampler; the upload is not.
        ComfyNode incomingDrive = TrimOfEntry("UnitTest_IcLoraB.safetensors").Image.Connection?.Node;
        Assert.IsNotType<BatchImagesNodeNode>(incomingDrive);
        string firstTargetStage = StageSampler(bridge, 1).Id;
        Assert.True(ReachesUpstream(bridge, incomingDrive, firstTargetStage));
        Assert.False(ReachesUpstream(bridge, padded, firstTargetStage));

        live.AssertAllLive([.. guideTrims, handle, padded]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An audio-consuming preset with a video drive takes the drive's audio stream only: the video
    /// is loaded and split, but no visual guide is built from its frames.
    /// </summary>
    [Fact]
    public async Task Custom_audio_consuming_ic_lora_feeds_reference_tokens_without_a_visual_guide()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraLipDub.safetensors");

        JObject entry = MakeIcLora("UnitTest_IcLoraLipDub", driveMediaData: DriveVideo);
        entry["preset"] = "custom-audio";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip([fixture.Stage()], entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());

        SwarmLoadVideoB64Node driveLoad = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Equal("QUJD", driveLoad.VideoBase64.LiteralAsString());
        GetVideoComponentsNode driveComponents = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Same(driveLoad, driveComponents.Video.Connection?.Node);

        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        LTXVAudioVAEEncodeNode encode = Assert.IsType<LTXVAudioVAEEncodeNode>(
            refTokens.AudioLatent.Connection?.Node);
        // Slot 1 of the components node is the audio stream; slot 0 is the frames a visual guide
        // would have taken.
        Assert.Same(driveComponents, encode.Audio.Connection?.Node);
        Assert.Equal(1, encode.Audio.Connection?.SlotIndex);
        Assert.True(ReachesUpstream(bridge, StageSampler(bridge, 0), refTokens.Id));

        live.AssertAllLive(driveLoad, driveComponents, refTokens, encode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// LipDub with an uploaded voice track conditions through reference tokens alone: no video is
    /// loaded and no guide frames are built.
    /// </summary>
    [Fact]
    public async Task Lipdub_drive_audio_feeds_audio_reference_tokens_without_loading_video_or_guide_frames()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraLipDub.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: DriveAudio,
            driveMediaFileName: "voice.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip([fixture.Stage()], entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());

        SwarmLoadAudioB64Node driveAudio = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.True(ReachesUpstream(bridge, refTokens.AudioLatent.Connection?.Node, driveAudio.Id));

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.True(ReachesUpstream(bridge, sampler.Positive.Connection?.Node, refTokens.Id));

        live.AssertAllLive(driveAudio, refTokens, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A stage-scoped LipDub entry conditions only its own stage's sampler; the unscoped stage's
    /// conditioning must not reach the reference tokens.
    /// </summary>
    [Fact]
    public async Task Lipdub_audio_reference_obeys_the_entry_stage_scope()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraLipDub.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: DriveAudio,
            driveMediaFileName: "voice.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        entry["stage"] = 1;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        SwarmKSamplerNode unscoped = StageSampler(bridge, 0);
        SwarmKSamplerNode scoped = StageSampler(bridge, 1);
        Assert.False(ReachesUpstream(bridge, unscoped, refTokens.Id));
        Assert.False(ReachesUpstream(bridge, unscoped, loader.Id));
        Assert.True(ReachesUpstream(bridge, scoped.Positive.Connection?.Node, refTokens.Id));
        Assert.Same(loader, scoped.Model.Connection?.Node);

        live.AssertAllLive(loader, refTokens, unscoped, scoped);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Across stages the voice track is materialised and encoded once, and the reference tokens
    /// rewriting the conditioning are one node too — the stages carry the same prompt, so there is
    /// one conditioning between them to rewrite.
    /// </summary>
    [Fact]
    public async Task Lipdub_all_stages_reuses_one_materialized_audio_sample()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraLipDub.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: DriveAudio,
            driveMediaFileName: "voice.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        // Both stages wrap the same conditioning in the same reference tokens, so there is one
        // wrapper; the claim is the sample beneath it, materialized once for the whole clip.
        IReadOnlyList<LTXVSetAudioRefTokensNode> refTokens =
            [.. bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>()];
        Assert.Single(refTokens);
        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.All(
            refTokens,
            tokens => Assert.Same(encode, tokens.AudioLatent.Connection?.Node));
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        live.AssertAllLive([.. refTokens, upload, encode]);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A clip that uploads its own audio track and a LipDub entry that uploads a speaker reference
    /// must stay separate. Which upload the reference tokens read is the claim: reading the clip's
    /// base track instead would condition on the wrong voice and look identical by node count.
    /// <para>Image-to-video, because clip 0 of a text-to-video request hits the P1 audio defect.</para>
    /// </summary>
    [Fact]
    public async Task Lipdub_drive_audio_stays_separate_from_the_clip_base_audio_upload()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraLipDub.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: "data:audio/wav;base64,RFJJVkU=",
            driveMediaFileName: "speaker.wav");
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject clip = IcLoraClip([fixture.Stage(control: 0.5)], entry);
        clip["duration"] = 1.0;
        clip["audioSource"] = Constants.AudioSourceUpload;
        clip["uploadedAudio"] = UploadedAudio("base.wav", payload: "QkFTRQ==");

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>().Count);
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        SwarmLoadAudioB64Node referenceUpload =
            bridge.Graph.FindNearestUpstream<SwarmLoadAudioB64Node>(
                refTokens.AudioLatent.Connection?.Node);
        Assert.NotNull(referenceUpload);
        Assert.Equal("RFJJVkU=", referenceUpload.AudioBase64.LiteralAsString());
        // The two tracks are never merged: the clip's own audio is conditioned separately.
        Assert.Empty(bridge.Graph.NodesOfType<AudioConcatNode>());

        live.AssertAllLive(refTokens, referenceUpload, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An audio entry and a visual entry on the same two-stage clip do not interfere: each stage
    /// gets both, while the two uploads are materialised once each.
    /// </summary>
    [Fact]
    public async Task Lipdub_and_visual_guide_coexist_across_refinement_without_extra_audio_wrapping()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraLipDub.safetensors")
            .InstallModel("LoRA", "UnitTest_IcLoraVisual.safetensors");

        JObject lipDub = MakeIcLora(
            "UnitTest_IcLoraLipDub",
            driveMediaData: DriveAudio,
            driveMediaFileName: "speaker.wav");
        lipDub["preset"] = "lipdub";
        lipDub["driveData"] = $"{IcLoraDriveData.Audio}";
        JObject visual = MakeIcLora(
            "UnitTest_IcLoraVisual",
            driveMediaData: DriveImage,
            driveMediaFileName: "guide.png");
        visual["preset"] = "ingredients";

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            lipDub,
            visual)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        IReadOnlyList<LTXVSetAudioRefTokensNode> refTokens =
            [.. bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>()];
        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides =
            [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()];
        Assert.Equal(2, refTokens.Count);
        Assert.Equal(2, guides.Count);
        // Each visual guide extends the latent, so each stage crops the guide frames back off —
        // named by the latent it crops, since a count alone would pass with both crops on one stage.
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);
        Assert.All(
            new[] { StageSampler(bridge, 0), StageSampler(bridge, 1) },
            stage => Assert.Single(
                bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
                crop => ReferenceEquals(
                    crop.LatentInput.Connection?.Node, OutputOf(bridge, stage))));
        // One upload each, and the voice track is encoded once for both stages.
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());

        // Reachability cannot separate the stages — stage 1 refines stage 0's latent, so all of
        // stage 0 is upstream of it. The direct wrapping order is the claim instead: each stage's
        // conditioning is its own token node over its own visual guide, in that order.
        SwarmKSamplerNode[] stages = [StageSampler(bridge, 0), StageSampler(bridge, 1)];
        LTXVSetAudioRefTokensNode[] wrappers = [.. stages.Select(stage =>
            Assert.IsType<LTXVSetAudioRefTokensNode>(stage.Positive.Connection?.Node))];
        Assert.Equal(refTokens.Count, wrappers.Distinct().Count());
        Assert.Equal(
            guides.Count,
            wrappers
                .Select(tokens => Assert.IsType<LTXAddVideoICLoRAGuideNode>(
                    tokens.PositiveInput.Connection?.Node))
                .Distinct()
                .Count());

        live.AssertAllLive([.. refTokens, .. guides, .. stages]);
        AssertShippable(bridge, workflow, live);
    }

    // ---- dropped entries ------------------------------------------------------------------

    /// <summary>
    /// The auto-model token only resolves through a preset name, so an entry without one is warned
    /// about and dropped — and the stage still generates, off an unpatched model.
    /// </summary>
    [Fact]
    public async Task Auto_ic_lora_without_a_preset_warns_and_drops_the_entry()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(IcLoraClip(
                    [fixture.Stage()],
                    MakeIcLora(IcLoraWeights.AutoModelToken)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertNoIcLoraNodes(bridge);
        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.IsType<UNETLoaderNode>(sampler.Model.Connection?.Node);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("[unspecified preset]", StringComparison.Ordinal));

        live.AssertAllLive(sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A known preset whose weights were never downloaded names the file it wanted, so the warning
    /// is actionable. The <c>.</c>-to-<c>_</c> spelling is what the downloader writes to disk.
    /// </summary>
    [Fact]
    public async Task Auto_ic_lora_with_uninstalled_weights_warns_and_drops_the_entry()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject entry = MakeIcLora(IcLoraWeights.AutoModelToken);
        entry["preset"] = "deblur";

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(IcLoraClip([fixture.Stage()], entry))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertNoIcLoraNodes(bridge);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(
                "LTX-2/IC-LoRA/ltx-2_3-22b-ic-lora-deblur-0_9",
                StringComparison.Ordinal));

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Auto_ic_lora_with_unknown_preset_warns_and_drops_the_entry()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject entry = MakeIcLora(IcLoraWeights.AutoModelToken);
        entry["preset"] = "unit-test-never-downloaded";

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(IcLoraClip([fixture.Stage()], entry))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertNoIcLoraNodes(bridge);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("unit-test-never-downloaded", StringComparison.Ordinal));

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An audio-consuming preset needs audio to consume. The weights are installed, so the drop is
    /// attributable to the drive media alone — and nothing of the entry reaches the graph, not even
    /// the upload.
    /// </summary>
    [Theory]
    [InlineData(null, "requires uploaded Audio Drive Media")]
    [InlineData("data:image/png;base64,QUJD", "cannot consume Audio data from Image media")]
    public async Task Lipdub_invalid_drive_media_warns_and_drops_the_entry(
        string driveMediaData,
        string expectedMessage)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraLipDub.safetensors");

        JObject entry = MakeIcLora("UnitTest_IcLoraLipDub", driveMediaData: driveMediaData);
        entry["preset"] = "lipdub";
        entry["driveData"] = $"{IcLoraDriveData.Audio}";

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(IcLoraClip([fixture.Stage()], entry))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        AssertNoIcLoraNodes(bridge);
        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(expectedMessage, StringComparison.Ordinal));

        live.AssertAllLive(StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A skip marker does not drop only its own stage. <c>ParseStages</c> breaks at the first
    /// skipped stage, so the clip keeps none of them, the timeline never activates, and the
    /// entry's <c>stage: 1</c> scope is never consulted — core generates the request alone.
    /// </summary>
    [Fact]
    public async Task Skip_marker_truncates_the_clips_stage_list()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo);
        entry["stage"] = 1;
        JObject skipped = fixture.Stage();
        skipped["skipped"] = true;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip([skipped, fixture.Stage()], entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Core's own text-to-video pass, and only it: a surviving stage 1 would add a second
        // sampler at StageSeed(1).
        SwarmKSamplerNode core = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(VideoStagesWorkflowFixture.Seed, core.NoiseSeed.LiteralAsLong());
        AssertNoIcLoraNodes(bridge);
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        live.AssertAllLive(core);
        AssertShippable(bridge, workflow, live);
    }
}
