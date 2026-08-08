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
/// The timeline's own output plumbing, generated through the real Comfy API POST path: the global
/// frame trim, multi-clip merging, and which node each intermediate and final publication is bound
/// to. Exercised on LTX-2 because it is the family with the richest output shape, but nothing here
/// is about LTX.
/// <para>
/// Sampler seeds identify stages: core's base pass is <c>Seed</c>, stages are
/// <c>Seed + 42 + StageId</c> flat across clips. Reachability upstream from a stage sampler proves
/// nothing about stage membership, because a refining stage re-encodes its predecessor's output and
/// so reaches that predecessor's whole branch.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public class TimelineOutputContractTests
{
    /// <summary>Frames a 0.6s LTX-2 source clip conforms to on the 8k+1 grid at 24 fps.</summary>
    private const int SourceClipFrames = 17;

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
    /// <c>donotsave</c> does not change the graph: core honours it above the graph, where the API
    /// returns a data URI instead of writing to disk. Generating both proves it for this shape
    /// only — the request builds no interpolation, so reinstating the suppression inside
    /// <c>FrameInterpolator</c> reds nothing here.
    /// </summary>
    [Fact]
    public async Task Do_not_save_generates_the_same_graph_as_a_saving_request()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            1.0,
            fixture.Stage(steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, steps: 9),
            fixture.Stage("PreviousStage", control: 0.5, steps: 10)));

        JObject saving = await fixture.GenerateImageToVideoAsync(
            document,
            post => post["outputintermediateimages"] = true);
        JObject notSaving = await fixture.GenerateImageToVideoAsync(
            document,
            post =>
            {
                post["outputintermediateimages"] = true;
                post["donotsave"] = true;
            });

        Assert.True(
            JToken.DeepEquals(saving, notSaving),
            "donotsave must generate a byte-identical workflow.");
    }

    /// <summary>
    /// With an image base model there is no root animation save to retarget, so the timeline
    /// publishes at core's result id and moves core's base image onto the id core uses for a
    /// superseded image. Both halves matter: publishing overwrites, so a timeline that skipped the
    /// move would ship the video having silently destroyed the still.
    /// </summary>
    [Fact]
    public async Task An_image_base_model_publishes_the_video_over_cores_final_output()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();

        // No videomodel: the timeline's stages drive the video, which is the whole reason core
        // numbers its base image as the result. Requesting one instead makes core do its own
        // demotion and this asserts nothing.
        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.Post(
                MakeDocument(MakeClip(1.0, fixture.Stage(steps: 8))),
                post => post["model"] = fixture.BaseModel.Name));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal("9", live.FinalVideoSave().Id);
        List<SwarmSaveImageWSNode> baseImages =
            [.. bridge.Graph.NodesOfType<SwarmSaveImageWSNode>()];
        Assert.True(
            baseImages.Count == 1,
            $"Expected core's base image save to survive, found {baseImages.Count}: publishing at "
                + "core's final-output id overwrites whatever already holds it.");
        Assert.Equal("30", baseImages[0].Id);

        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The text-to-video shape, where the timeline displaces core's video root entirely and the
    /// root's own save is the only one in the graph: it is retargeted onto whatever the timeline
    /// ends up publishing.
    /// </summary>
    [Fact]
    public async Task A_displaced_root_retargets_its_own_save()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip(1.0, fixture.Stage(steps: 8)))));
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
}
