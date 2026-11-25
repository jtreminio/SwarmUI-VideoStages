using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MiniMaxUpscaleContractTests
{
    private const string UpscaleModelFileName = "unit-test-upscaler.pth";

    [Theory]
    [InlineData(0, 1.5, 768, 576)]
    [InlineData(0.5, 1.5, 768, 576)]
    [InlineData(0.5, 1.25, 640, 480)]
    public async Task Learned_latent_upscale_preserves_audio_and_refines_at_target_dimensions(
        double control, double factor, int expectedWidth, int expectedHeight)
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        const string model = "H3/minimax_h3_latent_upscaler_3d_bf16.safetensors";
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage("PreviousStage", control: control, upscale: factor,
                upscaleMethod: $"latentmodel-{model}"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(clip), post => post["height"] = 384);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);
        LatentUpscaleModelLoaderNode loader = Assert.Single(
            bridge.Graph.NodesOfType<LatentUpscaleModelLoaderNode>());
        MiniMaxH3LatentUpscaleWithModelNode scale = Assert.Single(
            bridge.Graph.NodesOfType<MiniMaxH3LatentUpscaleWithModelNode>());
        Assert.Equal(model, loader.ModelName.LiteralAsString());
        Assert.Same(loader.LATENTUPSCALEMODEL, scale.Model.Connection);
        Assert.Equal(expectedWidth, scale.Width.LiteralAsInt());
        Assert.Equal(expectedHeight, scale.Height.LiteralAsInt());

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        Assert.True(ReachesUpstream(bridge, scale, first.Id));
        if (control > 0)
        {
            LTXVConcatAVLatentNode joint = JointLatentOf(StageSampler(bridge, 1));
            Assert.Same(scale.LATENT, joint.VideoLatent.Connection);
            Assert.NotNull(joint.AudioLatent.Connection);
            Assert.False(ReachesUpstream(bridge, joint.AudioLatent.Connection.Node, scale.Id));
            Assert.True(ReachesUpstream(bridge, joint.AudioLatent.Connection.Node, first.Id));
            live.AssertLive(joint);
        }
        else
        {
            Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
            Assert.NotNull(live.PublishedAudio());
            Assert.True(ReachesUpstream(bridge, live.PublishedAudio(), first.Id));
            Assert.False(ReachesUpstream(bridge, live.PublishedAudio(), scale.Id));
        }
        Assert.Empty(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageUpscaleWithModelNode>());
        live.AssertAllLive(first, loader, scale);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_zero_control_latent_upscale_still_scales_the_joint_latent()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0,
                upscale: 1.5,
                upscaleMethod: "latent-bislerp"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        LatentUpscaleByNode scale = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", scale.UpscaleMethod.LiteralAsString());
        Assert.Equal(1.5, scale.ScaleBy.LiteralAsDouble());

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 768);

        live.AssertLive(scale);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_pixel_upscale_resizes_the_decoded_frames_before_the_next_stage()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: "pixel-lanczos"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        ImageScaleNode scale = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 768);
        Assert.Equal(768, scale.Height.LiteralAsInt());
        Assert.Equal("lanczos", scale.UpscaleMethod.LiteralAsString());
        Assert.True(
            ReachesUpstream(bridge, scale, first.Id),
            "The upscale does not read the previous stage's output.");
        Assert.True(
            ReachesUpstream(bridge, JointLatentOf(second).VideoLatent.Connection?.Node, scale.Id),
            "The next stage's video latent is not built from the upscaled frames.");

        Assert.Empty(bridge.Graph.NodesOfType<LatentUpscaleByNode>());

        live.AssertAllLive(first, second, scale);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_latent_interpolation_upscale_resizes_only_the_video_half()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latent-bislerp"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        LatentUpscaleByNode scale = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", scale.UpscaleMethod.LiteralAsString());
        // Use a non-default factor to catch an unset input.
        Assert.Equal(2.0, scale.ScaleBy.LiteralAsDouble());
        Assert.True(ReachesUpstream(bridge, scale, first.Id));

        LTXVConcatAVLatentNode joint = JointLatentOf(second);
        Assert.Same(scale.LATENT, joint.VideoLatent.Connection);
        Assert.NotSame(scale.LATENT, joint.AudioLatent.Connection);

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 1024);
        Assert.NotNull(live.PublishedAudio());

        live.AssertAllLive(first, second, scale);
        AssertShippable(bridge, workflow, live);
    }

    [Fact]
    public async Task A_model_upscale_fits_its_output_to_the_next_stage_resolution()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: $"model-{UpscaleModelFileName}"));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);

        UpscaleModelLoaderNode loader = Assert.Single(
            bridge.Graph.NodesOfType<UpscaleModelLoaderNode>());
        Assert.Equal(UpscaleModelFileName, loader.ModelName.LiteralAsString());
        ImageUpscaleWithModelNode modelUpscale = Assert.Single(
            bridge.Graph.NodesOfType<ImageUpscaleWithModelNode>());
        Assert.Same(loader, modelUpscale.UpscaleModel.Connection?.Node);
        Assert.True(ReachesUpstream(bridge, modelUpscale, first.Id));

        ImageScaleNode fit = Assert.Single(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => ReferenceEquals(node.Image.Connection?.Node, modelUpscale));
        Assert.Equal(768, fit.Width.LiteralAsInt());
        Assert.Equal(768, fit.Height.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, JointLatentOf(second).VideoLatent.Connection?.Node, fit.Id),
            "The next stage's video latent is not built from the fitted upscale.");

        live.AssertAllLive(first, second, loader, modelUpscale, fit);
        AssertShippable(bridge, workflow, live);
    }
}
