using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Execution.Audio;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>MiniMax H3 clip boundaries: cut, crossfade, and audio carry across a join.</summary>
[Collection("VideoStagesTests")]
public class MiniMaxBoundaryContractTests
{
    [Fact]
    public async Task Two_clips_cut_together_into_one_published_video()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject shortClip = MakeClip(fixture.Stage());
        shortClip["duration"] = 0.2;
        JObject longClip = MakeClip(fixture.Stage());
        longClip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(shortClip, longClip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // ceil(s*fps)+1 gives 6 and 25 frames, which snap up to 22 and 39 on the 17k+5 grid.
        Assert.Equal(
            [22, 39],
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>()
                .Select(node => node.Length.LiteralAsInt())
                .Order());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, first.Id));
        Assert.True(ReachesUpstream(bridge, save.Images.Connection?.Node, second.Id));
        Assert.Equal(MiniMaxWorkflowFixture.Fps, save.Fps.LiteralAsDouble());

        live.AssertAllLive(first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The crossfade blend must be what the published save reads, not a parallel branch.
    /// </summary>
    [Fact]
    public async Task Two_clips_crossfade_through_the_shared_decoded_merge()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject shortClip = MakeClip(fixture.Stage());
        shortClip["duration"] = 0.2;
        shortClip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        shortClip["boundaryOutOverlap"] = 8;
        JObject longClip = MakeClip(fixture.Stage());
        longClip["duration"] = 1.0;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(shortClip, longClip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmRampMaskBatchNode ramp = Assert.Single(
            bridge.Graph.NodesOfType<SwarmRampMaskBatchNode>());
        Assert.Equal(8, ramp.Frames.LiteralAsInt());
        ImageCompositeMaskedNode blend = Assert.Single(
            bridge.Graph.NodesOfType<ImageCompositeMaskedNode>());
        Assert.Same(ramp, blend.Mask.Connection?.Node);

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection?.Node, blend.Id),
            "The published video does not read the crossfade merge.");

        // The overlap is consumed, not appended: 22 + 39 frames merged over 8.
        Assert.Equal(53, generator.CurrentMedia.Frames);
        Assert.Equal(WGNodeData.DT_AUDIO, generator.CurrentMedia.AttachedAudio?.DataType);

        live.AssertAllLive(ramp, blend);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The carry crosses a VAE round trip, which core's post-cleanup collapses.
    /// </summary>
    [Fact]
    public async Task Crossfade_audio_carry_conditions_the_next_clip()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject shortClip = MakeClip(fixture.Stage());
        shortClip["duration"] = 0.2;
        shortClip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        shortClip["boundaryOutOverlap"] = 8;
        shortClip["boundaryOutCarryAudio"] = true;
        JObject longClip = MakeClip(fixture.Stage());
        longClip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(shortClip, longClip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmSetAudioMaskWindowsNode carryMask = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSetAudioMaskWindowsNode>());
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        // The carry conditions the SECOND clip from the FIRST clip's tail, not the other way round.
        Assert.True(ReachesUpstream(bridge, second, carryMask.Id));
        Assert.False(ReachesUpstream(bridge, first, carryMask.Id));
        Assert.True(ReachesUpstream(bridge, carryMask, first.Id));

        // The preserved window is the 8-frame overlap at the head of the second clip, and the
        // carried track is cut from the tail of the first clip's 22 frames.
        JObject window = Assert.IsType<JObject>(
            Assert.Single(JArray.Parse(carryMask.Windows.LiteralAsString())));
        Assert.Equal(0, window.Value<double>("start"));
        Assert.Equal(0.33, window.Value<double>("end"), 6);
        // The graph also carries the merge's own tail trim, so select the carried cut by the mask
        // that reads it rather than by the values under test.
        TrimAudioDurationNode carryTrim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>(),
            node => ReachesUpstream(bridge, carryMask.Samples.Connection?.Node, node.Id));
        Assert.Equal(14 / 24.0, carryTrim.StartIndex.LiteralAsDouble().Value, 6);
        Assert.Equal(8 / 24.0, carryTrim.Duration.LiteralAsDouble().Value, 6);

        live.AssertAllLive(carryMask, carryTrim, first, second);
        AssertShippable(bridge, workflow, live);
    }
}
