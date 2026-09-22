using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxPromptImageIsolationTests
{
    private const string PromptImage =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ"
        + "AAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Global_prompt_images_do_not_become_implicit_clip_references(
        bool imageRoot,
        bool audioOnly,
        bool regional)
    {
        using MiniMaxWorkflowFixture fixture = imageRoot
            ? MiniMaxWorkflowFixture.CreateWithBaseModel()
            : MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;
        clip["audioOnly"] = audioOnly;
        if (imageRoot)
        {
            clip["keyframes"] = new JArray(MakeRef("edit0", frame: 16));
        }
        JObject document = MakeDocument(clip);
        JObject post = imageRoot ? fixture.ImageToVideoPost(document) : fixture.Post(document);
        post["promptimages"] = new JArray(PromptImage, PromptImage);
        if (regional)
        {
            post["prompt"] = "A field <region:0,0,0.5,1> a red kite <region:0.5,0,0.5,1> a blue kite";
            post["maskbehavior"] = "Simple Latent";
        }

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                post,
                extraSteps: imageRoot ? [PublishBase2EditImageStep(0)] : []);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = StageSampler(bridge, 0);
        Assert.All(bridge.Graph.Nodes.Values.Where(
            node => node.ClassTypeName == "SwarmMiniMaxH3CollectReferences"),
            reference => Assert.False(ReachesUpstream(
                bridge, sampler.Positive.Connection?.Node, reference.Id)));
        Assert.Equal(2, generator.UserInput.Get(T2IParamTypes.PromptImages).Count);
        if (imageRoot)
        {
            MiniMaxH3AddGuideNode guide = Assert.Single(
                bridge.Graph.NodesOfType<MiniMaxH3AddGuideNode>());
            Assert.Equal(16, guide.FrameIdx.LiteralAsInt());
            Assert.Same(guide, sampler.Positive.Connection?.Node);
            live.AssertLive(guide);
        }
        if (audioOnly)
        {
            ComfyNode save = Assert.Single(bridge.Graph.Nodes.Values,
                node => node.ClassTypeName.Contains("SaveAudio", StringComparison.Ordinal));
            Assert.True(ReachesUpstream(bridge, save, sampler.Id));
        }
        else
        {
            live.AssertLive(sampler);
        }
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }
}
