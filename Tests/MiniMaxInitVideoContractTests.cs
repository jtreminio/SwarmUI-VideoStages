using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxInitVideoContractTests
{
    [Theory]
    [InlineData(294)]
    [InlineData(295)]
    public async Task Refine_preserves_measured_source_frames_after_sampling(int sourceFrames)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(control: 0),
            fixture.Stage("PreviousStage", control: 0.4, upscale: 1.75));
        clip["duration"] = 12.3;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "refine-source",
            ["startSeconds"] = 1 / 24.0,
            ["durationSeconds"] = (sourceFrames + 1) / 24.0,
            ["lengthSeconds"] = sourceFrames / 24.0,
        };

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);
        SwarmFrameWindowNode source = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>(),
            node => node.ImagesInput.Connection?.Node is SwarmVideoResampleFPSNode);
        Assert.Equal(1, source.StartFrame.LiteralAsInt());
        Assert.Equal(sourceFrames, source.FrameCount.LiteralAsInt());
        Assert.Equal(sourceFrames, generator.CurrentMedia.Frames);
        TrimAudioDurationNode audio = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.Equal(sourceFrames / 24.0, audio.Duration.LiteralAsDouble());
        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        if (sourceFrames == 295)
        {
            SwarmFrameWindowNode pad = Assert.Single(
                bridge.Graph.NodesOfType<SwarmFrameWindowNode>(),
                node => node.Id != source.Id);
            Assert.Equal(311, pad.FrameCount.LiteralAsInt());
            Assert.True(ReachesUpstream(bridge, sampler, pad.Id));
            ImageFromBatchNode trim = Assert.IsType<ImageFromBatchNode>(
                live.FinalVideoSave().Images.Connection?.Node);
            Assert.Equal(sourceFrames, trim.Length.LiteralAsInt());
            Assert.True(ReachesUpstream(bridge, trim, sampler.Id));
        }
        else
        {
            Assert.Single(bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
            Assert.True(ReachesUpstream(bridge, live.FinalVideoSave(), sampler.Id));
        }
        live.AssertAllLive(source, audio, sampler);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Without comfy_loadimage_b64, core loads uploads without audio or an FPS reference.
    /// </summary>
    [Fact]
    public async Task Init_video_refines_conformed_footage_and_its_audio()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(control: 0.5),
            fixture.Stage("PreviousStage", control: 0.5));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Equal(24, window.StartFrame.LiteralAsInt());
        Assert.Equal(39, window.FrameCount.LiteralAsInt());

        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == window.Id);
        Assert.Equal(MiniMaxWorkflowFixture.Width, scale.Width.LiteralAsInt());
        Assert.Equal(MiniMaxWorkflowFixture.Height, scale.Height.LiteralAsInt());

        TrimAudioDurationNode sourceAudio = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => node.StartIndex.LiteralAsDouble() == 1);
        Assert.Equal(39 / 24.0, sourceAudio.Duration.LiteralAsDouble().Value, 6);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count());
        Assert.Equal(4, first.StartAtStep.LiteralAsInt());
        Assert.Equal(4, second.StartAtStep.LiteralAsInt());

        Assert.True(ReachesUpstream(bridge, first, window.Id));
        Assert.True(ReachesUpstream(bridge, first, sourceAudio.Id));
        Assert.Single(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());

        // Core collapses separate/concat pairs and prunes unused concats.
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>().Count());

        live.AssertAllLive(window, scale, sourceAudio, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Legacy uploads without a measured length retain the authored frame count.
    /// </summary>
    [Fact]
    public async Task Init_video_passthrough_publishes_conformed_footage_unsampled()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(control: 0));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 1.0,
        };

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmFrameWindowNode window = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        Assert.Equal(24, window.StartFrame.LiteralAsInt());
        Assert.Equal(25, window.FrameCount.LiteralAsInt());

        ImageScaleNode conform = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Image.Connection?.Node.Id == window.Id);
        SwarmSaveAnimationWSNode save = live.FinalVideoSave();
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, conform.Id));

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>(),
            node => ReachesUpstream(bridge, save.Images.Connection?.Node, node.Id));
        Assert.Empty(bridge.Graph.NodesOfType<SwarmEmptyMiniMaxH3LatentAVNode>());

        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.True(ReachesUpstream(
            bridge,
            bridge.ResolvePath((JArray)generator.CurrentMedia.Path)?.Node,
            window.Id));
        Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath((JArray)generator.CurrentMedia.AttachedAudio.Path)?.Node);

        live.AssertAllLive(window, conform);
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// <c>RootPlanCompiler</c> keeps the host root for an image-to-video request with a generated
    /// clip, and core's cleanup whitelist excludes checkpoint loaders and samplers — so an unused
    /// base generation would survive.
    /// </summary>
    [Fact]
    public async Task Image_to_video_with_an_uploaded_first_frame_builds_no_unused_base_generation()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["keyframes"] = new JArray(UploadedReference("RklSU1Q=", fromEnd: false));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmLoadImageB64Node upload = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        live.AssertLive(upload);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// H3 conditions on keyframes through a node that takes the latent as an input, so a first
    /// keyframe composes with source footage rather than competing for its slot. Both keyframes
    /// survive beside an init video.
    /// </summary>
    [Fact]
    public async Task Init_video_keeps_both_keyframes()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage(control: 0.5));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
        };
        clip["keyframes"] = new JArray(
            UploadedReference("RklSU1Q=", fromEnd: false),
            UploadedReference("TEFTVA==", fromEnd: true));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        Assert.NotNull(keyframes.FirstFrame.Connection);
        Assert.NotNull(keyframes.LastFrame.Connection);

        live.AssertLive(keyframes);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Refine Video must reframe the original keyframe at the surviving stage's resolution.
    /// </summary>
    [Fact]
    public async Task A_refined_source_clip_reasserts_its_first_keyframe_at_the_refine_resolution()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            // Control 0 is what Refine Video writes onto the stage already spent.
            fixture.Stage(control: 0),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "source.mp4",
        };
        clip["keyframes"] = new JArray(UploadedReference("RklSU1Q="));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Only the upscaling stage samples, so its keyframe must be 768 pixels.
        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        SwarmFrameImageNode framed = Assert.IsType<SwarmFrameImageNode>(
            keyframes.FirstFrame.Connection?.Node);
        Assert.Equal(768, framed.Width.LiteralAsInt());
        Assert.Equal(768, framed.Height.LiteralAsInt());
        Assert.Equal(
            "RklSU1Q=",
            Assert.IsType<SwarmLoadImageB64Node>(framed.ImagesInput.Connection?.Node)
                .ImageBase64.LiteralAsString());

        live.AssertAllLive(keyframes, framed);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_refined_source_clip_reuses_its_soundtrack_when_the_upload_is_empty()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(control: 0),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5));
        clip["duration"] = 1.0;
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,ESIz",
            ["fileName"] = "refine-source",
        };
        clip["audioSource"] = MediaSource.Upload;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        TrimAudioDurationNode sourceAudio = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        VAEEncodeAudioNode encode = Assert.Single(
            bridge.Graph.NodesOfType<VAEEncodeAudioNode>());
        SwarmKSamplerNode refine = Assert.Single(
            bridge.Graph.NodesOfType<SwarmKSamplerNode>());

        Assert.True(ReachesUpstream(bridge, encode, sourceAudio.Id));
        Assert.True(ReachesUpstream(bridge, refine, sourceAudio.Id));

        live.AssertAllLive(sourceAudio, encode, refine);
        AssertShippable(bridge, workflow, live);
    }
}
