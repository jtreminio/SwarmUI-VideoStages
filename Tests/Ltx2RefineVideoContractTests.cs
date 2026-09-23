using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class Ltx2RefineVideoContractTests
{
    [Fact]
    public async Task Refining_a_rendered_stage_upscales_its_video_and_preserves_its_soundtrack()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.CreateWithBaseModel();
        JObject clip = SourceClip(1.0, 0.0,
            fixture.Stage(control: 0),
            fixture.Stage("PreviousStage", control: 0.3, upscale: 2.0,
                upscaleMethod: LtxV23SpatialUpscaler, steps: 16));
        clip["audioSource"] = MediaSource.Native;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(clip)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = StageSampler(bridge, 1);
        Assert.Equal(11, sampler.StartAtStep.LiteralAsInt());
        LTXVLatentUpsamplerNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());
        GetVideoComponentsNode source = Assert.Single(
            bridge.Graph.NodesOfType<GetVideoComponentsNode>());
        Assert.True(ReachesUpstream(bridge, upscale, source.Id));
        Assert.True(ReachesUpstream(bridge, sampler, upscale.Id));
        Assert.Equal(1024, generator.CurrentMedia.Width);
        Assert.Equal(1024, generator.CurrentMedia.Height);

        SetLatentNoiseMaskNode preserveAudio = Assert.IsType<SetLatentNoiseMaskNode>(
            JointLatentOf(sampler).AudioLatent.Connection?.Node);
        Assert.Equal(0.0, Assert.IsType<SolidMaskNode>(preserveAudio.Mask.Connection?.Node)
            .Value.LiteralAsDouble());
        LTXVAudioVAEEncodeNode audio = Assert.IsType<LTXVAudioVAEEncodeNode>(
            preserveAudio.Samples.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, audio, source.Id));
        Assert.Same(OutputOf(bridge, sampler).AudioLatent,
            Assert.IsType<LTXVAudioVAEDecodeNode>(live.PublishedAudio()).Samples.Connection);
        Assert.DoesNotContain(
            generator.UserInput.ExtraMeta.TryGetValue("parser_warnings", out object warnings)
                ? Assert.IsType<List<string>>(warnings)
                : [],
            warning => warning.Contains("no uploaded file is attached"));

        live.AssertAllLive(source, upscale, audio, preserveAudio, sampler);
        AssertShippable(bridge, workflow, live);
    }
}
