using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;
using VideoStages.Architectures.Ltx2.Runtime.Chain;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class MixedArchitectureAudioCompatibilityTests
{
    [Fact]
    public void Post_chain_reuse_requires_the_same_video_and_audio_vae_outputs()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator generator = UnitTestStubs.StubGenerator(workflow);
        generator.CurrentMedia = new WGNodeData(
            new JArray("5", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null);
        LtxPostVideoChain chain = LtxPostVideoChain.TryCapture(generator);

        WGNodeData producingVideoVae = new(
            new JArray("1", 2), generator, WGNodeData.DT_VAE, null);
        WGNodeData producingAudioVae = new(
            new JArray("2", 0), generator, WGNodeData.DT_AUDIOVAE, null);
        WGNodeData otherVideoVae = new(
            new JArray("other-video-vae", 0), generator, WGNodeData.DT_VAE, null);
        WGNodeData otherAudioVae = new(
            new JArray("other-audio-vae", 0), generator, WGNodeData.DT_AUDIOVAE, null);

        Assert.NotNull(chain);
        Assert.True(chain.MatchesLoadedVaes(null, producingVideoVae, producingAudioVae));
        Assert.False(chain.MatchesLoadedVaes(null, otherVideoVae, producingAudioVae));
        Assert.False(chain.MatchesLoadedVaes(null, producingVideoVae, otherAudioVae));
    }

    [Fact]
    public async Task Ltx_reencodes_a_previous_MiniMax_clips_audio_with_its_own_vae()
    {
        using LtxAndMiniMaxFixture fixture = new();
        JObject miniMax = MakeClip(
            0.6,
            MakeStage(
                fixture.SecondModel.Name,
                steps: 7,
                cfgScale: 1));
        JObject ltx = MakeClip(
            0.6,
            MakeStage(
                fixture.Model.Name,
                steps: 9));
        ltx["initVideo"] = new JObject
        {
            ["source"] = MediaSource.PreviousClip,
            ["startSeconds"] = 0,
        };

        JObject workflow = await ComfyWorkflowApiTestHarness.GenerateAsync(
            fixture.ImageToVideoPost(
                MakeDocument(miniMax, ltx),
                post => post["videomodel"] = fixture.SecondModel.Name));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode miniMaxSampler = StageSampler(bridge, 0);
        SwarmKSamplerNode ltxSampler = StageSampler(bridge, 1);
        LTXVSeparateAVLatentNode miniMaxOutput = OutputOf(bridge, miniMaxSampler);
        LTXVSeparateAVLatentNode ltxOutput = OutputOf(bridge, ltxSampler);
        LTXVConcatAVLatentNode ltxInput = Assert.IsType<LTXVConcatAVLatentNode>(
            ltxSampler.LatentImage.Connection?.Node);

        SetLatentNoiseMaskNode mask = Assert.IsType<SetLatentNoiseMaskNode>(
            ltxInput.AudioLatent.Connection?.Node);
        LTXVAudioVAEEncodeNode encode = Assert.IsType<LTXVAudioVAEEncodeNode>(
            mask.Samples.Connection?.Node);

        Assert.NotSame(miniMaxOutput.AudioLatent, ltxInput.AudioLatent.Connection);
        Assert.True(ReachesUpstream(bridge, encode, miniMaxSampler.Id));
        Assert.Empty(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        VAEDecodeTiledNode published = Assert.IsType<VAEDecodeTiledNode>(
            live.FinalVideoSave().Images.Connection?.Node);
        Assert.Same(ltxOutput, published.Samples.Connection?.Node);
        AssertShippable(bridge, workflow, live);
    }
}
