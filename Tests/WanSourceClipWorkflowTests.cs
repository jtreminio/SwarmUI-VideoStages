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
/// Clips that enter from uploaded footage: the conform chain, what a passthrough publishes, and how
/// a partial-control stage windows its source.
/// </summary>
[Collection("VideoStagesTests")]
public class WanSourceClipWorkflowTests
{
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
                fixture.ImageToVideoPost(MakeDocument(WanWorkflowFixture.SourceClip(
                    fixture.Stage(control: 1, steps: 10),
                    fixture.Stage("PreviousStage", control: 0.5, steps: 12),
                    fixture.Stage("PreviousStage", control: 0, steps: 13)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        // The source replaces core's video root; core's base image pass survives it, protected by
        // core's own image save.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        Wan22ImageToVideoLatentNode latent = Assert.Single(
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>());
        Assert.Same(latent, first.LatentImage.Connection?.Node);
        Assert.Equal(WanWorkflowFixture.SourceClipFrames, latent.Length.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, latent.StartImage.Connection?.Node, window.Id));

        // control 0.5 over 12 steps starts halfway through the schedule.
        Assert.Equal(6, second.StartAtStep.LiteralAsInt());
        VAEEncodeNode reEncode = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, reEncode, first.Id));
        Assert.Equal(WanWorkflowFixture.SourceClipFrames, generator.CurrentMedia.Frames);

        live.AssertAllLive(window, latent, reEncode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    // ---- init-video clips ---------------------------------------------------------------

    /// <summary>
    /// A partially-controlled source stage reads the conformed footage twice, for two different
    /// jobs: one frame for the conditioning donor and the whole window for the latent it refines.
    /// They are separate nodes — folding them together would either encode one frame or condition
    /// on all of them. The request's global trim then shortens the published result, not the
    /// window.
    /// <para>
    /// The arms differ only in whether the upload carries a file name: it is metadata on the
    /// materialized <c>ImageFile</c>, so the same graph must come out either way, and the authored
    /// name must not appear in it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_partial_source_stage_conditions_on_one_frame_and_encodes_the_window(
        bool withFileName)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(
            WanWorkflowFixture.SourceClip(withFileName, fixture.Stage(control: 0.5, steps: 10)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["trimvideostartframes"] = 4));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        // The stage plus core's base image pass.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        // control 0.5 over 10 steps.
        Assert.Equal(5, stage.StartAtStep.LiteralAsInt());

        // The inline payload is what loads, with or without a name beside it.
        string authoredData = (string)document["clips"][0]["initVideo"]["data"];
        Assert.Equal(
            authoredData[(authoredData.IndexOf(',') + 1)..],
            Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>())
                .VideoBase64.LiteralAsString());
        string authoredFileName = (string)document["clips"][0]["initVideo"]["fileName"];
        Assert.Equal(withFileName, authoredFileName is not null);
        if (authoredFileName is not null)
        {
            Assert.DoesNotContain(
                authoredFileName,
                workflow.ToString(),
                StringComparison.Ordinal);
        }

        VAEEncodeNode encode = Assert.IsType<VAEEncodeNode>(stage.LatentImage.Connection?.Node);
        ImageFromBatchNode encoded = Assert.IsType<ImageFromBatchNode>(
            encode.Pixels.Connection?.Node);
        Assert.Equal(0, encoded.BatchIndex.LiteralAsInt());
        Assert.Equal(WanWorkflowFixture.SourceClipFrames, encoded.Length.LiteralAsInt());
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
        Assert.Equal(WanWorkflowFixture.SourceClipFrames - 4, generator.CurrentMedia.Frames);
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
                    MakeDocument(WanWorkflowFixture.SourceClip(fixture.Stage(control: 1, steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
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
                MakeDocument(WanWorkflowFixture.SourceClip(fixture.Stage(control: 0.5, steps: 10)))),
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

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
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
                    MakeDocument(WanWorkflowFixture.SourceClip(fixture.Stage(control: 0, steps: 10))),
                    post => post["trimvideostartframes"] = 4));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge, expectedFrames: 16);
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
        JObject document = MakeDocument(WanWorkflowFixture.SourceClip(
            fixture.Stage(control: 0, steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, steps: 10)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document, post => post["outputintermediateimages"] = true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
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
        JObject source = WanWorkflowFixture.SourceClip(fixture.Stage(control: 0.5, steps: 10));
        JObject document = sourceFirst
            ? MakeDocument(source, generated)
            : MakeDocument(generated, source);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = WanWorkflowFixture.AssertSourceConformChain(bridge);
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
}
