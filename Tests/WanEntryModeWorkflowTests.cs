using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Planning;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// How a Wan clip enters: which root it takes over, what its native latent is built from, and the
/// per-profile differences between the 5B and 14B entry shapes.
/// </summary>
[Collection("VideoStagesTests")]
public class WanEntryModeWorkflowTests
{
    /// <summary>
    /// A checkpoint list <see cref="WanWorkflowFixture"/> has no factory for. Both architectures'
    /// support models are installed so the same fixture serves the cross-architecture timelines;
    /// each installer replaces the shared VAE handler, so WAN's VAEs are re-added last.
    /// </summary>
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

        ImageScaleNode framing = WanWorkflowFixture.FirstFrameFraming(conditioning.StartImage.Connection?.Node);
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
            Diagnostics(generator),
            diagnostic => diagnostic.Code == "effective-request.video-swap-ignored");

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
            WanWorkflowFixture.FirstFrameFraming(latent.StartImage.Connection?.Node).Image.Connection?.Node);
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

    // ---- timeline merge ----------------------------------------------------------------

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
}
