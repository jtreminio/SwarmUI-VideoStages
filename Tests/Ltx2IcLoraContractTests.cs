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

/// <summary>Where a stage's positive conditioning is asserted by identity, it holds because no clip
/// here authors a retake: LtxAudioWindowMasker would legitimately interpose its window node.</summary>
[Collection("VideoStagesTests")]
public class Ltx2IcLoraContractTests
{
    private const string DriveVideo = "data:video/mp4;base64,QUJD";
    private const string OtherDriveVideo = "data:video/mp4;base64,REVG";
    private const string DriveImage = "data:image/png;base64,QUJD";
    private const string DriveAudio = "data:audio/wav;base64,QUJD";

    private static JObject MakeIcLora(
        string lora,
        string source = MediaSource.Upload,
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

    private static void AssertNoIcLoraNodes(WorkflowBridge bridge)
    {
        Assert.Empty(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideAdvancedNode>());
    }

    private static LTXAddVideoICLoRAGuideNode OnlyGuide(WorkflowBridge bridge) =>
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

    private static bool GuideDrivenBy(
        WorkflowBridge bridge,
        LTXAddVideoICLoRAGuideNode guide,
        ComfyNode wanted) =>
        ReachesUpstream(bridge, guide.Image.Connection?.Node, wanted.Id);

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

    /// <summary>Production tries the underscored name first and falls back to the dotted one,
    /// which predates the `.`→`_` rename. Both arms owe the same wiring contract.</summary>
    [Theory]
    [InlineData("ltx-2_3-22b-ic-lora-deblur-0_9")]
    [InlineData("ltx-2.3-22b-ic-lora-deblur-0.9")]
    public async Task Auto_ic_lora_resolves_either_installed_weight_name(string stem)
    {
        string installed = $"LTX-2/IC-LoRA/{stem}.safetensors";
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", installed);

        JObject entry = MakeIcLora(IcLoraWeights.AutoModelToken, driveMediaData: DriveVideo);
        entry["preset"] = "deblur";

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(IcLoraClip([fixture.Stage()], entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LTXICLoRALoaderModelOnlyNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());
        Assert.Equal(installed, loader.LoraName.LiteralAsString());

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.Same(loader, sampler.Model.Connection?.Node);
        Assert.Single(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        live.AssertAllLive(loader, sampler);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Same(guide, scoped.Positive.Connection?.Node);

        live.AssertAllLive(loader, guide, unscoped, scoped);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.All(stages, stage => Assert.Same(loader, stage.Model.Connection?.Node));
        Assert.Equal(
            2,
            stages.Select(stage => stage.Positive.Connection?.Node?.Id).Distinct().Count());

        live.AssertAllLive([loader, .. stages]);
        AssertShippable(bridge, workflow, live);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Stage_input_source_drives_guide_from_the_stages_input_frames(int stageId)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraA",
            source: MediaSource.Incoming,
            driveData: IcLoraDriveData.Visual);
        entry["stage"] = stageId;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(control: 0.5), fixture.Stage("PreviousStage", control: 0.5)],
            entry)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());

        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        SwarmKSamplerNode scoped = StageSampler(bridge, stageId);
        Assert.True(ReachesUpstream(bridge, scoped, guide.Id));

        ImageScaleNode resize = Assert.IsType<ImageScaleNode>(GuideFraming(guide));
        Assert.Equal(VideoStagesWorkflowFixture.Width, resize.Width.LiteralAsInt());
        Assert.Equal(VideoStagesWorkflowFixture.Height, resize.Height.LiteralAsInt());
        Assert.Equal("center", resize.Crop.LiteralAsString());

        Assert.True(GuideDrivenBy(bridge, guide, BaseImage(bridge)));
        Assert.Equal(stageId == 1, GuideDrivenBy(bridge, guide, StageSampler(bridge, 0)));

        live.AssertAllLive(guide, resize, scoped);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Incoming_control_signal_is_built_from_each_stages_input()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(control: 0.5), fixture.Stage("PreviousStage", control: 0.5)],
            MakeIcLora(
                "UnitTest_IcLoraA",
                source: MediaSource.Incoming,
                controlType: Constants.IcLoraControlCanny,
                driveData: IcLoraDriveData.Visual))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode[] stages = [StageSampler(bridge, 0), StageSampler(bridge, 1)];
        LTXAddVideoICLoRAGuideNode[] guides = [.. stages.Select(stage =>
            Assert.IsType<LTXAddVideoICLoRAGuideNode>(stage.Positive.Connection?.Node))];
        CannyNode[] controls = [.. bridge.Graph.NodesOfType<CannyNode>()];
        Assert.Equal(2, controls.Length);
        Assert.Equal(2, guides.Distinct().Count());

        CannyNode firstControl = bridge.Graph.FindNearestUpstream<CannyNode>(
            guides[0].Image.Connection?.Node);
        CannyNode secondControl = bridge.Graph.FindNearestUpstream<CannyNode>(
            guides[1].Image.Connection?.Node);
        Assert.Contains(firstControl, controls);
        Assert.Contains(secondControl, controls);
        Assert.NotSame(firstControl, secondControl);
        Assert.True(ReachesUpstream(bridge, firstControl.Image.Connection?.Node, BaseImage(bridge).Id));
        Assert.False(ReachesUpstream(bridge, firstControl.Image.Connection?.Node, stages[0].Id));
        Assert.True(ReachesUpstream(bridge, secondControl.Image.Connection?.Node, stages[0].Id));

        live.AssertAllLive([.. stages, .. guides, .. controls]);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Stage_input_source_works_on_a_latent_upscale_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject entry = MakeIcLora(
            "UnitTest_IcLoraA",
            source: MediaSource.Incoming,
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

        LatentUpscaleByNode scaler = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Same(OutputOf(bridge, first).VideoLatent, scaler.Samples.Connection);
        Assert.True(ReachesUpstream(bridge, second.LatentImage.Connection?.Node, scaler.Id));

        VAEDecodeTiledNode decode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeTiledNode>(),
            node => GuideDrivenBy(bridge, guide, node));
        Assert.Same(OutputOf(bridge, first).VideoLatent, decode.Samples.Connection);
        Assert.False(ReachesUpstream(bridge, scaler, decode.Id));

        live.AssertAllLive(guide, decode, scaler, first, second);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Equal(1024, resize.Width.LiteralAsInt());
        Assert.Equal(1024, resize.Height.LiteralAsInt());
        Assert.Equal("center", resize.Crop.LiteralAsString());

        live.AssertAllLive(guide, resize, StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Uploaded_drive_media_uses_the_clips_green_fit_method()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject clip = IcLoraClip(
            [fixture.Stage()],
            MakeIcLora("UnitTest_IcLoraA", driveMediaData: DriveVideo));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

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
        Assert.Equal(1, firstGuide.LatentDownscaleFactor.Connection?.SlotIndex);
        Assert.Equal(0.7, firstGuide.Strength.LiteralAsDouble());
        Assert.Equal(0.7, secondGuide.Strength.LiteralAsDouble());

        live.AssertAllLive([.. loaders, .. guides]);
        AssertShippable(bridge, workflow, live);
    }

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
        SwarmLoadVideoB64Node load = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Equal("REVG", load.VideoBase64.LiteralAsString());
        Assert.True(GuideDrivenBy(bridge, OnlyGuide(bridge), load));

        live.AssertAllLive(loader, load, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task Uploaded_drive_and_control_chain_are_shared_across_stages()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", "UnitTest_IcLoraA.safetensors");

        JObject workflow = await fixture.GenerateAsync(MakeDocument(IcLoraClip(
            [fixture.Stage(), fixture.Stage()],
            MakeIcLora(
                "UnitTest_IcLoraA",
                controlType: Constants.IcLoraControlCanny,
                driveMediaData: DriveVideo))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        CannyNode control = Assert.Single(bridge.Graph.NodesOfType<CannyNode>());
        Assert.Single(bridge.Graph.NodesOfType<LTXICLoRALoaderModelOnlyNode>());

        IReadOnlyList<LTXAddVideoICLoRAGuideNode> guides =
            [.. bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>()];
        Assert.Equal(2, guides.Count);
        Assert.True(ReachesUpstream(bridge, control.Image.Connection?.Node, components.Id));
        Assert.All(guides, guide => Assert.True(GuideDrivenBy(bridge, guide, control)));

        live.AssertAllLive([.. guides, components, control]);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Equal(25, repeat.Amount.LiteralAsInt());
        LTXAddVideoICLoRAGuideNode guide = OnlyGuide(bridge);
        Assert.Same(repeat, guide.Image.Connection?.Node);

        live.AssertAllLive(repeat, guide);
        AssertShippable(bridge, workflow, live);
    }

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
            source: MediaSource.Incoming,
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
        Assert.Equal(0, firstFrame.BatchIndex.LiteralAsInt());
        Assert.Equal(1, firstFrame.Length.LiteralAsInt());

        ImageFromBatchNode TrimOfEntry(string loraFile) => Assert.IsType<ImageFromBatchNode>(
            Assert.Single(
                guides,
                guide => guide.LatentDownscaleFactor.Connection?.Node
                    is LTXICLoRALoaderModelOnlyNode loader
                    && loader.LoraName.LiteralAsString() == loraFile)
                .Image.Connection?.Node);

        BatchImagesNodeNode padded = Assert.IsType<BatchImagesNodeNode>(
            TrimOfEntry("UnitTest_IcLoraA.safetensors").Image.Connection?.Node);
        Assert.Equal(2, padded.Images.Count);
        Assert.Same(handle, padded.Images[0].Connection?.Node);
        SwarmLoadVideoB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.True(ReachesUpstream(bridge, padded.Images[1].Connection?.Node, upload.Id));

        ComfyNode incomingDrive = TrimOfEntry("UnitTest_IcLoraB.safetensors").Image.Connection?.Node;
        Assert.IsNotType<BatchImagesNodeNode>(incomingDrive);
        string firstTargetStage = StageSampler(bridge, 1).Id;
        Assert.True(ReachesUpstream(bridge, incomingDrive, firstTargetStage));
        Assert.False(ReachesUpstream(bridge, padded, firstTargetStage));

        live.AssertAllLive([.. guideTrims, handle, padded]);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Same(driveComponents, encode.Audio.Connection?.Node);
        Assert.Equal(1, encode.Audio.Connection?.SlotIndex);
        Assert.Same(refTokens, StageSampler(bridge, 0).Positive.Connection?.Node);

        live.AssertAllLive(driveLoad, driveComponents, refTokens, encode);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Same(refTokens, sampler.Positive.Connection?.Node);

        live.AssertAllLive(driveAudio, refTokens, sampler);
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Same(refTokens, scoped.Positive.Connection?.Node);
        Assert.Same(loader, scoped.Model.Connection?.Node);

        live.AssertAllLive(loader, refTokens, unscoped, scoped);
        AssertShippable(bridge, workflow, live);
    }

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
        LTXVSetAudioRefTokensNode refTokens = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioRefTokensNode>());
        SwarmLoadAudioB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        LTXVAudioVAEEncodeNode encode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());
        Assert.Same(encode, refTokens.AudioLatent.Connection?.Node);

        // The node counts above are satisfied by a stage-0-only application. Identity, not
        // reachability: two samplers reaching the node proves nothing about reuse, since a
        // refining stage reaches its predecessor's branch through the latent.
        SwarmKSamplerNode[] stages = [StageSampler(bridge, 0), StageSampler(bridge, 1)];
        Assert.All(
            stages,
            stage => Assert.Same(refTokens, stage.Positive.Connection?.Node));
        Assert.Empty(bridge.Graph.NodesOfType<LTXAddVideoICLoRAGuideNode>());

        live.AssertAllLive([refTokens, upload, encode, .. stages]);
        AssertShippable(bridge, workflow, live);
    }

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
        clip["audioSource"] = MediaSource.Upload;
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
        Assert.Empty(bridge.Graph.NodesOfType<AudioConcatNode>());

        live.AssertAllLive(refTokens, referenceUpload, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

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
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVCropGuidesNode>().Count);
        Assert.All(
            new[] { StageSampler(bridge, 0), StageSampler(bridge, 1) },
            stage => Assert.Single(
                bridge.Graph.NodesOfType<LTXVCropGuidesNode>(),
                crop => ReferenceEquals(
                    crop.LatentInput.Connection?.Node, OutputOf(bridge, stage))));
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        Assert.Single(bridge.Graph.NodesOfType<LTXVAudioVAEEncodeNode>());

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

        SwarmKSamplerNode core = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(VideoStagesWorkflowFixture.Seed, core.NoiseSeed.LiteralAsLong());
        AssertNoIcLoraNodes(bridge);
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        live.AssertAllLive(core);
        AssertShippable(bridge, workflow, live);
    }
}
