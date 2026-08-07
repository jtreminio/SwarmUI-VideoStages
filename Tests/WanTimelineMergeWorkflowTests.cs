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
/// Multi-clip Wan timelines: hard-cut joins, conforming an upscaled clip back before the merge, the
/// global trim, and mixed-family timelines that keep each clip's provenance apart.
/// </summary>
[Collection("VideoStagesTests")]
public class WanTimelineMergeWorkflowTests
{
    /// <summary>
    /// A checkpoint list <see cref="WanWorkflowFixture"/> has no factory for. Both architectures'
    /// support models are installed so the same fixture serves the cross-architecture timelines;
    /// each installer replaces the shared VAE handler, so WAN's VAEs are re-added last.
    /// </summary>
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
    /// node either way, taking its frames off the front of the whole timeline.
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
    /// Text-to-video hard cuts have no host image to donate, so both 5B clips sample the one bare
    /// native latent and are told apart only by seed. Core's own video root — which the timeline
    /// replaces — leaves nothing of its own behind, and no silent audio track is invented for an
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

        Wan22ImageToVideoLatentNode latent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        // Absent, not merely unconnected: a literal here would still be a donor.
        Assert.False(latent.StartImage.HasValue);
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        // The first stage takes core's root decode over rather than building beside it, so the id
        // core published survives — decoding this stage now. Core's own pass is gone all the same:
        // nothing in the graph decodes anything but a stage sampler.
        Assert.NotNull(coreRootId);
        Assert.Same(
            first,
            RequireTypedNode<VAEDecodeNode>(bridge, coreRootId).Samples.Connection?.Node);
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
    /// the conformed window for WAN. The request's trim lands once, on the merged timeline and
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
        JObject wanClip = WanWorkflowFixture.SourceClip(
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
        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
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
        Assert.Equal(WanWorkflowFixture.SourceClipFrames / 24.0, silence.Duration.LiteralAsDouble()!.Value, 6);
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
    /// <c>donotsave</c> is honoured above the graph — <c>T2IAPI</c> returns a data URI rather than
    /// writing to disk — so a mixed timeline publishes exactly as it would without the flag. It once
    /// stripped the timeline's own saves, which in text-to-video left ComfyUI with no output node at
    /// all to reject (P5 in <c>nonversioned/20260804-production-findings.md</c>).
    /// </summary>
    [Fact]
    public async Task A_do_not_save_mixed_timeline_publishes_exactly_as_a_saving_request_does()
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

        // Two saves, not three: clip 0's intermediate, then the timeline's publication, which is
        // also clip 1's intermediate rather than a node of its own.
        live.AssertAllLive(StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }
}
