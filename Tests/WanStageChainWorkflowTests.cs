using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Chains of Wan stages inside one clip: handing off through a decode and re-encode, upscaling
/// between stages, and which passes publish an intermediate.
/// </summary>
[Collection("VideoStagesTests")]
public class WanStageChainWorkflowTests
{
    // ---- stage handoffs -----------------------------------------------------------------

    /// <summary>
    /// Two stages on distinct checkpoints run as two ordinary passes joined by a decode and a
    /// re-encode: each keeps its own sampler settings and its own section of the prompt, and the
    /// second conditions on the first's decoded first frame. The pair is deliberately not a
    /// high-then-low continuation — that needs a high-noise predecessor, which stage 0 is not.
    /// </summary>
    [Fact]
    public async Task Two_stages_on_distinct_checkpoints_hand_off_through_a_decode_and_re_encode()
    {
        using MultiModelFixture fixture = MultiModelFixture.CreateWithBaseModel(
            WanWorkflowFixture.Wan22I2v14bFixturePath,
            WanWorkflowFixture.Wan22I2v14bLowNoiseFixturePath);
        JObject document = MakeDocument(MakeClip(
            MakeStage(fixture.Model.Name, "Generated", control: 1, steps: 10, cfgScale: 4),
            MakeStage(
                fixture.Models[1].Name,
                "PreviousStage",
                control: 0.35,
                steps: 12,
                cfgScale: 6.5,
                sampler: "dpmpp_2m",
                scheduler: "karras")));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    document,
                    post => post["prompt"] =
                        "global <videoclip[0,0]>first-stage-prompt"
                        + " <videoclip[0,1]>second-stage-prompt"));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(10, first.Steps.LiteralAsInt());
        Assert.Equal(4.0, first.Cfg.LiteralAsDouble());
        Assert.Equal("euler", first.SamplerName.LiteralAsString());
        Assert.Equal("normal", first.Scheduler.LiteralAsString());
        Assert.Equal(0, first.StartAtStep.LiteralAsInt());
        Assert.Equal(12, second.Steps.LiteralAsInt());
        Assert.Equal(6.5, second.Cfg.LiteralAsDouble());
        Assert.Equal("dpmpp_2m", second.SamplerName.LiteralAsString());
        Assert.Equal("karras", second.Scheduler.LiteralAsString());
        // control 0.35 over 12 steps starts at floor(12 * 0.65).
        Assert.Equal(7, second.StartAtStep.LiteralAsInt());

        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bFixturePath),
            ModelBranchOf(first).Loader.UnetName.LiteralAsString());
        Assert.Equal(
            Path.GetFileName(WanWorkflowFixture.Wan22I2v14bLowNoiseFixturePath),
            ModelBranchOf(second).Loader.UnetName.LiteralAsString());

        VAEEncodeNode handoff = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        VAEDecodeNode firstDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => decode.Samples.Connection?.Node == first);
        Assert.True(ReachesUpstream(bridge, handoff, firstDecode.Id));
        WanImageToVideoNode secondConditioning = Assert.IsType<WanImageToVideoNode>(
            second.Positive.Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge, secondConditioning.StartImage.Connection?.Node, firstDecode.Id));
        Assert.Equal(
            "first-stage-prompt",
            ConditioningText(first.Positive.Connection?.Node, negative: false));
        Assert.Equal(
            "second-stage-prompt",
            ConditioningText(secondConditioning, negative: false));

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Equal(25, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);

        live.AssertAllLive(handoff, firstDecode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    // ---- stage upscales -----------------------------------------------------------------

    /// <summary>
    /// A stage's pixel upscale resizes the decoded handoff before the stage runs, so the next
    /// sampler's latent and its conditioning frame both come from the enlarged video and the clip
    /// publishes at the new size.
    /// </summary>
    [Fact]
    public async Task A_pixel_upscale_resizes_the_decoded_handoff_before_the_next_stage()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(steps: 10),
            fixture.Stage("PreviousStage", control: 0.5, upscale: 1.5, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        VAEDecodeNode handoffDecode = Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            decode => decode.Samples.Connection?.Node == first);
        ImageScaleNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Image.Connection?.Node == handoffDecode);
        Assert.Equal(768, upscale.Width.LiteralAsInt());
        Assert.Equal(768, upscale.Height.LiteralAsInt());
        Assert.Equal("lanczos", upscale.UpscaleMethod.LiteralAsString());

        VAEEncodeNode reEncode = Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, reEncode, upscale.Id));
        WanImageToVideoNode conditioning = Assert.IsType<WanImageToVideoNode>(
            second.Positive.Connection?.Node);
        Assert.True(ReachesUpstream(
            bridge, conditioning.StartImage.Connection?.Node, upscale.Id));
        Assert.Equal(768, generator.CurrentMedia.Width);
        Assert.Equal(768, generator.CurrentMedia.Height);

        live.AssertAllLive(upscale, reEncode, first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A passthrough stage that only upscales adds no sampler of its own: the resize is the clip's
    /// published output.
    /// </summary>
    [Fact]
    public async Task A_pixel_upscale_on_a_passthrough_stage_is_the_clips_output()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(steps: 10),
            fixture.Stage(
                "PreviousStage",
                control: 0,
                upscale: 1.5,
                upscaleMethod: "pixel-bicubic",
                steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        // Core's base pass plus this one stage; the passthrough contributes none.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count);
        ImageScaleNode upscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.UpscaleMethod.LiteralAsString() == "bicubic");
        Assert.Equal(768, upscale.Width.LiteralAsInt());
        Assert.Equal(768, upscale.Height.LiteralAsInt());
        Assert.True(ReachesUpstream(bridge, upscale, stage.Id));
        Assert.Same(upscale, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Equal(768, generator.CurrentMedia.Width);
        Assert.Equal(768, generator.CurrentMedia.Height);

        live.AssertAllLive(upscale, stage);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// WAN has no latent-space upscaler, so a stage authoring one is warned about and dropped
    /// rather than being passed to <c>ImageScale</c> as an upscale method it would reject. The
    /// pixel-upscale tests above are the control that a real resize does reach the graph.
    /// </summary>
    [Theory]
    [InlineData("latent-bislerp", "bislerp")]
    [InlineData("latentmodel-unit-upscaler.safetensors", "unit-upscaler.safetensors")]
    public async Task A_latent_upscale_is_warned_about_and_emits_no_pixel_scaler(
        string upscaleMethod,
        string rejectedPixelMethod)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(steps: 10),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: upscaleMethod,
                steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.UpscaleMethod.LiteralAsString() == rejectedPixelMethod);
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            scale => scale.Width.LiteralAsInt() == 768);
        Assert.Contains(
            Diagnostics(generator),
            diagnostic =>
                diagnostic.Code == "effective-request.unsupported-latent-upscale-ignored"
                && diagnostic.Message.Contains(upscaleMethod, StringComparison.Ordinal));
        Assert.Equal(512, generator.CurrentMedia.Width);

        live.AssertAllLive(StageSampler(bridge, 0), second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Three chained stages each publish their own intermediate, but the request's frame trim
    /// belongs to the finished timeline: exactly one trim node, on the last stage's output, and it
    /// is what the published save reads.
    /// </summary>
    [Fact]
    public async Task Three_chained_stages_publish_intermediates_and_only_the_last_is_trimmed()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject document = MakeDocument(MakeClip(
            fixture.Stage(control: 1, steps: 8),
            fixture.Stage("PreviousStage", control: 0.5, steps: 10),
            fixture.Stage("PreviousStage", control: 0.25, steps: 12)));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(document, post =>
                {
                    post["outputintermediateimages"] = true;
                    post["trimvideostartframes"] = 4;
                }));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        SwarmKSamplerNode third = StageSampler(bridge, 2);
        // floor(10 * 0.5) and floor(12 * 0.75).
        Assert.Equal(5, second.StartAtStep.LiteralAsInt());
        Assert.Equal(9, third.StartAtStep.LiteralAsInt());
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(second.LatentImage.Connection?.Node),
            first.Id));
        Assert.True(ReachesUpstream(
            bridge,
            Assert.IsType<VAEEncodeNode>(third.LatentImage.Connection?.Node),
            second.Id));
        // The chain's whole VAE census: one decode per stage and no more, and the two re-encodes
        // the two handoffs need. Nothing else may add a round trip.
        AssertOneDecodePerStage(bridge, first, second, third);
        Assert.Equal(2, bridge.Graph.NodesOfType<VAEEncodeNode>().Count);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        SwarmSaveAnimationWSNode[] saves = [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        Assert.Equal(3, saves.Length);
        Assert.Same(trim, live.FinalVideoSave().Images.Connection?.Node);
        Assert.Equal(2, saves.Count(save => save.Images.Connection?.Node is VAEDecodeNode));
        Assert.All(saves, save => Assert.Equal(24.0, save.Fps.LiteralAsDouble()));
        Assert.Equal(21, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertAllLive(trim, first, second, third);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }
}
