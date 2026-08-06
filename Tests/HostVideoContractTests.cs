using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using VideoStages.Generated;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Two things that are not about any one architecture, generated through the real Comfy API POST
/// path: the timeline's output plumbing (global frame trim, multi-clip merging, which node each
/// publication is bound to), exercised on LTX-2; and the generic host-video fallback that drives
/// any video model the extension has no module for.
/// <para>
/// The host-video fallback is selected by elimination — <c>Ltx2ArchitectureModule</c> only claims
/// model class <c>lightricks-ltx-video-2-3</c>, so even an LTX-2 checkpoint that is not 2.3 lands
/// here. Its descriptor declares no features and a frame grid of 1, so everything a timeline can
/// author beyond model/steps/control/upscale is warned about and dropped.
/// </para>
/// <para>
/// Sampler seeds identify stages: core's base pass is <c>Seed</c>, stages are
/// <c>Seed + 42 + StageId</c> flat across clips. Reachability upstream from a stage sampler proves
/// nothing about stage membership, because a refining stage re-encodes its predecessor's output and
/// so reaches that predecessor's whole branch.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class HostVideoContractTests
{
    /// <summary>Frames a 0.6s LTX-2 source clip conforms to on the 8k+1 grid at 24 fps.</summary>
    private const int SourceClipFrames = 17;

    /// <summary>A ControlNet guide payload; <c>ValidateParam</c> rejects anything under 10 base64
    /// characters.</summary>
    private const string ControlNetVideoPayload =
        "data:video/mp4;base64,AAAAAAAAAAAAAAAAAAAAAA==";

    /// <summary>
    /// The decode that reads a stage's sampled video latent. A multi-stage clip grows a second,
    /// identical decode of the same latent for the next stage's guide — core does not dedup
    /// sibling <c>VAEDecodeTiled</c> nodes — so callers that may hit that shape must select the
    /// decode they mean rather than calling this.
    /// </summary>
    private static VAEDecodeTiledNode VideoDecodeOf(
        WorkflowBridge bridge,
        SwarmKSamplerNode sampler)
    {
        LTXVSeparateAVLatentNode split = OutputOf(bridge, sampler);
        return Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeTiledNode>(),
            decode => ReferenceEquals(decode.Samples.Connection?.Node, split));
    }

    /// <summary>The stage latent a decode-fed publication was built from.</summary>
    private static ComfyNode PublishedLatentOf(SwarmSaveAnimationWSNode save) =>
        Assert.IsType<VAEDecodeTiledNode>(save.Images.Connection?.Node)
            .Samples.Connection?.Node;

    /// <summary>Whichever stage split a decoded audio stream came from.</summary>
    private static ComfyNode LtxAudioLatentOf(ComfyNode decode) =>
        Assert.IsType<LTXVAudioVAEDecodeNode>(decode).Samples.Connection?.Node;

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

    private static IReadOnlyList<PlanDiagnostic> Diagnostics(WorkflowGenerator generator) =>
        generator.RequireVideoExecutionPlanContext().Plan.Diagnostics;

    // ---- timeline output plumbing -------------------------------------------------------

    /// <summary>
    /// One authored stage produces exactly one video pass alongside core's base pass, sampling the
    /// joint AV latent the stage built. Its <c>control: 0.5</c> is discarded: stage 0 of a clip
    /// with no source has nothing to refine, so the parser forces it to 1.0 and the schedule starts
    /// at step 0.
    /// </summary>
    [Fact]
    public async Task A_single_stage_clip_adds_one_video_pass_to_cores_base_pass()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(1.0, fixture.Stage(control: 0.5, steps: 8)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode baseSampler = fixture.BaseSampler(bridge);
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Equal(8, stage.Steps.LiteralAsInt());
        Assert.Equal(0, stage.StartAtStep.LiteralAsInt());
        Assert.IsType<LTXVImgToVideoInplaceNode>(
            JointLatentOf(stage).VideoLatent.Connection?.Node);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(25, generator.CurrentMedia.Frames);

        live.AssertAllLive(baseSampler, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A second stage refines the first: both halves of its joint latent trace to the first
    /// stage's split — the video half through an in-place image conditioning, the audio half
    /// directly — and its schedule starts halfway through, from <c>control: 0.5</c> over 10 steps.
    /// </summary>
    [Fact]
    public async Task A_later_stage_refines_the_previous_stages_split_latent()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(MakeClip(
                1.0,
                fixture.Stage(steps: 8),
                fixture.Stage("PreviousStage", control: 0.5, steps: 10))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        LTXVSeparateAVLatentNode firstOutput = OutputOf(bridge, first);
        LTXVConcatAVLatentNode secondInput = JointLatentOf(second);
        Assert.Same(
            firstOutput,
            Assert.IsType<LTXVImgToVideoInplaceNode>(secondInput.VideoLatent.Connection?.Node)
                .LatentInput.Connection?.Node);
        Assert.Same(firstOutput, secondInput.AudioLatent.Connection?.Node);
        Assert.Equal(5, second.StartAtStep.LiteralAsInt());
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        live.AssertAllLive(first, second, secondInput);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The request's global frame trim wraps the terminal stage only. The intermediate publication
    /// this request also asks for is bound to stage 0's own decode and is untrimmed, so the trim is
    /// not a whole-graph rewrite; the attached audio is trimmed to the same span.
    /// </summary>
    [Fact]
    public async Task The_global_frame_trim_wraps_only_the_terminal_stage()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(
                        1.0,
                        fixture.Stage(steps: 8),
                        fixture.Stage("PreviousStage", control: 0.5, steps: 10))),
                    post =>
                    {
                        post["trimvideostartframes"] = 2;
                        post["trimvideoendframes"] = 3;
                        post["outputintermediateimages"] = true;
                    }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(2, trim.TrimStart.LiteralAsInt());
        Assert.Equal(3, trim.TrimEnd.LiteralAsInt());
        Assert.Same(
            OutputOf(bridge, second),
            Assert.IsType<VAEDecodeTiledNode>(trim.Image.Connection?.Node).Samples.Connection?.Node);

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        SwarmSaveAnimationWSNode intermediate = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>(),
            save => save.Id != published.Id);
        Assert.Same(trim, published.Images.Connection?.Node);
        Assert.Same(OutputOf(bridge, first), PublishedLatentOf(intermediate));

        TrimAudioDurationNode audioTrim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Same(audioTrim, published.Audio.Connection?.Node);
        Assert.Same(
            OutputOf(bridge, second),
            Assert.IsType<LTXVAudioVAEDecodeNode>(audioTrim.Audio.Connection?.Node)
                .Samples.Connection?.Node);

        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(20, generator.CurrentMedia.Frames);

        live.AssertAllLive(trim, audioTrim, first, second);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// Two clips assemble into one image batch and one audio concat, each fed by exactly the two
    /// clips' terminal decodes, and the published save reads both.
    /// </summary>
    [Fact]
    public async Task Two_clips_assemble_into_one_image_batch_and_one_audio_concat()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(
                    MakeClip(1.0, fixture.Stage(steps: 10)),
                    MakeClip(1.0, fixture.Stage(steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.Equal(
            new[] { VideoDecodeOf(bridge, first).Id, VideoDecodeOf(bridge, second).Id }.Order(),
            bridge.Graph.FindUpstream(merge).Select(node => node.Id).Order());

        AudioConcatNode concat = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());
        Assert.Same(OutputOf(bridge, first), LtxAudioLatentOf(concat.Audio1.Connection?.Node));
        Assert.Same(OutputOf(bridge, second), LtxAudioLatentOf(concat.Audio2.Connection?.Node));

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Same(merge, published.Images.Connection?.Node);
        Assert.Same(concat, published.Audio.Connection?.Node);
        Assert.Equal(50, generator.CurrentMedia.Frames);
        // Core's base pass plus one pass per clip; the stage seeds are flat across clips.
        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        live.AssertAllLive(merge, concat, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// With more than one clip the global trim moves past the merge: it wraps the merged batch
    /// once rather than each clip, and the published frame count drops by the trim once.
    /// </summary>
    [Fact]
    public async Task The_global_frame_trim_wraps_the_assembled_timeline_once()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(
                        MakeClip(1.0, fixture.Stage(steps: 10)),
                        MakeClip(1.0, fixture.Stage(steps: 10))),
                    post =>
                    {
                        post["trimvideostartframes"] = 2;
                        post["trimvideoendframes"] = 3;
                    }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        AudioConcatNode concat = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());
        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        TrimAudioDurationNode audioTrim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Same(merge, trim.Image.Connection?.Node);
        Assert.Same(concat, audioTrim.Audio.Connection?.Node);

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Same(trim, published.Images.Connection?.Node);
        Assert.Same(audioTrim, published.Audio.Connection?.Node);
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        // 25 frames per clip, trimmed once.
        Assert.Equal(45, generator.CurrentMedia.Frames);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(24, generator.CurrentMedia.FPS);

        live.AssertAllLive(trim, audioTrim, merge, concat);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Core's own pre-save frame trim is displaced by the timeline rather than left in place for
    /// the extension's trim to stack on top of. The text-to-video shape is the only one that
    /// reaches it: core emits its trim only when <c>IsVideoModel() &amp;&amp; !formedFromSingleImage
    /// &amp;&amp; !willHaveFollowupVideo</c>, and an image-to-video request always fails the last —
    /// its <c>videomodel</c> is exactly the follow-up video that clause names. The spy is the
    /// positive control — core's wrapper genuinely exists before the timeline runs.
    /// </summary>
    [Fact]
    public async Task Cores_own_pre_save_trim_is_displaced_rather_than_stacked_on()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        string coreTrimId = null;
        // After core's pre-video-save prep (priority 10), before the timeline claims the root.
        WorkflowGenerator.WorkflowGenStep spy = new(
            g =>
            {
                using WorkflowBridge staged = WorkflowBridge.Create(g.Workflow);
                coreTrimId = Assert.Single(
                    staged.Graph.NodesOfType<SwarmTrimFramesNode>()).Id;
            },
            Constants.WorkflowStepPriority.CoreImageToVideo - 0.5);

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(
                        MakeClip(1.0, fixture.Stage(steps: 10)),
                        MakeClip(1.0, fixture.Stage(steps: 10))),
                    post =>
                    {
                        post["trimvideostartframes"] = 2;
                        post["trimvideoendframes"] = 3;
                    }),
                extraSteps: [spy]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.NotNull(coreTrimId);
        Assert.NotEqual(coreTrimId, trim.Id);
        Assert.Same(merge, trim.Image.Connection?.Node);
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(45, generator.CurrentMedia.Frames);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        // Text-to-video has no base pass, so the two stage samplers are the whole graph.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);

        live.AssertAllLive(trim, merge);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Three stages with intermediate output publish three videos, each bound to a different
    /// stage's sampled latent, and the last of them is the request's own published media.
    /// </summary>
    [Fact]
    public async Task Intermediate_publications_stay_bound_to_distinct_stage_latents()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(
                        1.0,
                        fixture.Stage(steps: 8),
                        fixture.Stage("PreviousStage", control: 0.5, steps: 9),
                        fixture.Stage("PreviousStage", control: 0.5, steps: 10))),
                    post => post["outputintermediateimages"] = true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmSaveAnimationWSNode[] saves =
            [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(3, saves.Length);
        Assert.Equal(
            Enumerable.Range(0, 3)
                .Select(stageId => OutputOf(bridge, StageSampler(bridge, stageId)).Id)
                .Order(),
            saves.Select(save => PublishedLatentOf(save).Id).Order());

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Equal(
            new JArray(published.Images.Connection!.Node.Id, 0),
            generator.CurrentMedia.Path);
        Assert.Same(OutputOf(bridge, StageSampler(bridge, 2)), PublishedLatentOf(published));

        live.AssertAllLive(published);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }

    /// <summary>
    /// <c>donotsave</c> does not change the graph at all: it is honoured above the graph, where
    /// the API returns a data URI instead of writing to disk. So the image-to-video shape still
    /// publishes all three stage videos alongside core's base-image save.
    /// </summary>
    [Fact]
    public async Task Do_not_save_publishes_the_timeline_exactly_as_a_saving_request_does()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(MakeClip(
                1.0,
                fixture.Stage(steps: 8),
                fixture.Stage("PreviousStage", control: 0.5, steps: 9),
                fixture.Stage("PreviousStage", control: 0.5, steps: 10))),
            post =>
            {
                post["outputintermediateimages"] = true;
                post["donotsave"] = true;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(3, bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>().Count);
        Assert.Single(bridge.Graph.NodesOfType<SwarmSaveImageWSNode>());

        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }

    /// <summary>
    /// The text-to-video shape, where the timeline displaces core's video root entirely and the
    /// root's own save is the only one in the graph: it is retargeted onto whatever the timeline
    /// ends up publishing. There is no core base-image save here to carry the request, so a
    /// suppressed save would leave ComfyUI a prompt with no output node to run at all.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_displaced_root_retargets_its_own_save(bool doNotSave)
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    MakeDocument(MakeClip(1.0, fixture.Stage(steps: 8))),
                    post => post["donotsave"] = doNotSave));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Core's displaced root pass is gone; only the stage's own sampler remains.
        SwarmKSamplerNode stage = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(VideoStagesWorkflowFixture.StageSeed(0), stage.NoiseSeed.LiteralAsLong());

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Equal(
            new JArray(published.Images.Connection.Node.Id, 0),
            generator.CurrentMedia.Path);

        live.AssertAllLive(stage, published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An LTX-2 source clip refines its uploaded footage through the same conform chain the generic
    /// runtime uses, and takes core's video root with it, so the clip's own pass is the only video
    /// sampler. Unlike the generic path it also carries the footage's audio into the joint latent,
    /// which is the architecture's contribution rather than the conform chain's.
    /// </summary>
    [Fact]
    public async Task An_LTX_source_clip_refines_the_conformed_footage_with_its_own_audio()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(SourceClip(
                    0.6,
                    0.0,
                    fixture.Stage(control: 0.5, steps: 10)))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = AssertSourceConformChain(bridge, 0, SourceClipFrames);
        // Core's video root is displaced, but its base image pass is protected by core's own image
        // save, so the clip's pass is the only *video* one rather than the only one.
        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        Assert.Equal(5, stage.StartAtStep.LiteralAsInt());

        LTXVConcatAVLatentNode joint = JointLatentOf(stage);
        VAEEncodeNode reEncode = Assert.IsType<VAEEncodeNode>(joint.VideoLatent.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, reEncode, window.Id));
        GetVideoComponentsNode components = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.True(ReachesUpstream(
            bridge,
            joint.AudioLatent.Connection?.Node,
            components.Id));

        Assert.Equal(SourceClipFrames, generator.CurrentMedia.Frames);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        Assert.NotNull(live.PublishedAudio());

        live.AssertAllLive(window, joint, reEncode, stage);
        AssertShippable(bridge, workflow, live);
    }

    // ---- generic host-video fallback ----------------------------------------------------

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
    /// An LTX-2 checkpoint that is not 2.3 falls to the generic runtime, which lets core build the
    /// joint audio/video latent it always builds for the family. The document's own fps wins over
    /// the request's for everything in the graph, while the request keeps the value the user set.
    /// The generic runtime declares no audio support, so the audio half is sampled and then
    /// dropped: nothing decodes it and the publication is video-only.
    /// </summary>
    [Fact]
    public async Task Ltx2_text_entry_builds_the_host_joint_latent_but_publishes_video_only()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateNonV23();
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
    /// save muxes. No capability warning — audio tracks are model-independent.
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

        Assert.DoesNotContain(
            Diagnostics(generator),
            diagnostic => diagnostic.Code == "effective-request.audio-segments-ignored");

        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadAudioB64Node>());
        EmptyAudioNode bed = Assert.Single(bridge.Graph.NodesOfType<EmptyAudioNode>());
        // The bed is the clip's own decoded length: 25 frames at 24 fps.
        Assert.Equal(25 / 24d, bed.Duration.LiteralAsDouble() ?? 0, 6);
        AudioMergeNode mix = Assert.Single(bridge.Graph.NodesOfType<AudioMergeNode>());
        Assert.Same(bed, mix.Audio1.Connection?.Node);
        Assert.Same(mix, live.PublishedAudio());
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
        Assert.Single(generator.GetTimelineSpec().TimelineAudioSegments);

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
        stage["refStrengths"] = new JArray(0.7);
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
        List<string> warnings = RequestWarnings(generator.UserInput);
        foreach (string parameter in new[]
        {
            "'Video2Video Creativity'", "'Prompt Audios'", "'Prompt Videos'",
        })
        {
            Assert.Contains(
                warnings,
                warning => warning.Contains(parameter, StringComparison.Ordinal));
        }

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
            warnings,
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

        public override JObject Post(JObject document, Action<JObject> customize = null) =>
            base.Post(document, post =>
            {
                post["clipvisionmodel"] = TestModelFactory.Hunyuan15ClipVisionFileName;
                customize?.Invoke(post);
            });

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
