using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2GeneratedWorkflowContractTests
{
    /// <summary>
    /// The smoke test the LTX-2 conversion chunks rest on: an LTX-2.3 request survives the whole
    /// production step list, resolves all four support-model stubs without reaching for a
    /// download, and hands the timeline's sampler a joint audio/video latent.
    /// </summary>
    [Fact]
    public async Task Basic_text_to_video_can_be_generated_from_the_Comfy_API_POST_body()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        JObject clip = MakeClip(fixture.Stage());
        clip["duration"] = 1.0;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // The timeline hands the request back decoded video, not the latent it sampled.
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);

        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(fixture.ExpectedGeneratedFrames, latent.Length.LiteralAsInt());

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(Ltx2WorkflowFixture.Steps, sampler.Steps.LiteralAsInt());
        LTXVConcatAVLatentNode joint = Assert.IsType<LTXVConcatAVLatentNode>(
            sampler.LatentImage.Connection?.Node);
        Assert.Same(latent, joint.VideoLatent.Connection?.Node);

        // The 2.3 branch: a dual CLIP loader (Gemma + text projection) and a separate audio VAE.
        Assert.Single(bridge.Graph.NodesOfType<DualCLIPLoaderNode>());
        Assert.Single(bridge.Graph.NodesOfType<SwarmLTXVAudioVAELoaderNode>());
        LTXVAudioVAEDecodeNode audioDecode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVAudioVAEDecodeNode>());

        live.AssertAllLive(latent, joint, sampler, audioDecode);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }
}
