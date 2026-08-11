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
/// MiniMax H3 entry from existing footage: conforming an uploaded source clip, and what core's own
/// base pass contributes once a stage supplies its own first frame.
/// </summary>
[Collection("VideoStagesTests")]
public class MiniMaxInitVideoContractTests
{
    /// <summary>
    /// Without <c>comfy_loadimage_b64</c> core loads the upload as a bare image with no attached
    /// audio and no FPS reference, so only this path exercises the conform chain end to end.
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

        // The refine stage inherits both through stage 0's latent, so asserting them on stage 1
        // too would be true by transitivity.
        Assert.True(ReachesUpstream(bridge, first, window.Id));
        Assert.True(ReachesUpstream(bridge, first, sourceAudio.Id));
        Assert.Single(bridge.Graph.NodesOfType<VAEEncodeAudioNode>());

        // One joint latent per stage survives core's post-cleanup, which collapses
        // LTXVSeparateAVLatent over LTXVConcatAVLatent and prunes unused concats.
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<SetLatentNoiseMaskNode>().Count());

        live.AssertAllLive(window, scale, sourceAudio, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// No stage generates, so the frame count stays authored (25) instead of snapping to the
    /// 17k+5 grid.
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
        Assert.Empty(bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());

        // The handoff the next clip or stage would read: unsnapped frame count, and the source's
        // own audio still attached rather than a generated track.
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
        clip["frameRefs"] = new JArray(UploadedReference("RklSU1Q=", fromEnd: false));

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
        clip["frameRefs"] = new JArray(
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
    /// The Refine Video shape: a low-resolution pass generated from a first keyframe becomes the
    /// source, its spent stage drops to Control 0, and the surviving stage upscales. The keyframe
    /// has to reach that stage at ITS resolution — reframed for the upscale, not for the pass that
    /// produced the footage — or frame 0 drifts off the image the clip was authored around.
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
        clip["frameRefs"] = new JArray(UploadedReference("RklSU1Q="));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Only the upscaling stage samples, so only it takes a keyframe — at 768, not the 512 the
        // source footage was generated at.
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
}
