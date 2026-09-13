using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxRootOwnershipTests
{
    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public async Task A_clip_image_reference_cannot_keep_the_replaced_Ltx_root_alive(string source)
    {
        using LtxAndMiniMaxFixture fixture = new();
        JObject clip = MakeClip(8, MakeStage(
            fixture.SecondModel.Name, steps: 5, cfgScale: 1));
        clip["references"] = new JArray(
            MakeClipReference("image", source: source),
            MakeClipReference("image", source: "Upload"));
        JObject document = MakeDocument(clip);
        document["width"] = 640;
        document["height"] = 960;

        JObject workflow = await fixture.GenerateAsync(document);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Same(StageSampler(bridge, 0), sampler);
        Assert.Equal(5, sampler.Steps.LiteralAsInt());
        SwarmEmptyMiniMaxH3LatentAVNode latent = Assert.IsType<SwarmEmptyMiniMaxH3LatentAVNode>(
            sampler.LatentImage.Connection?.Node);
        Assert.Equal(640, latent.Width.LiteralAsInt());
        Assert.Equal(960, latent.Height.LiteralAsInt());
        Assert.Equal(209, latent.Length.LiteralAsInt());
        UNETLoaderNode loader = Assert.Single(bridge.Graph.NodesOfType<UNETLoaderNode>());
        Assert.Equal(fixture.SecondModel.Name, loader.UnetName.LiteralAsString());
        Assert.DoesNotContain(bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "SwarmMiniMaxH3CollectReferences");

        live.AssertAllLive(sampler, latent, loader);
        AssertShippable(bridge, workflow, live);
    }
}
