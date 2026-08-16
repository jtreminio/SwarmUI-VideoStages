using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxImageRootKeyframeContractTests
{
    [Fact]
    public async Task An_explicit_middle_keyframe_replaces_the_image_root_fallback()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 5.0;
        clip["frameRefs"] = new JArray(MakeRef("Refiner", frame: 21));

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        MiniMaxH3AddGuideNode guide = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3AddGuideNode>());
        Assert.Equal(21, guide.FrameIdx.LiteralAsInt());
        Assert.Same(guide, StageSampler(bridge, 0).Positive.Connection?.Node);

        live.AssertLive(guide);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task An_image_root_without_an_explicit_keyframe_falls_back_to_first_frame()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.CreateWithBaseModel();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateImageToVideoAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        Assert.NotNull(keyframes.FirstFrame.Connection);
        Assert.Empty(bridge.Graph.NodesOfType<MiniMaxH3AddGuideNode>());

        live.AssertLive(keyframes);
        AssertShippable(bridge, workflow, live);
    }
}
