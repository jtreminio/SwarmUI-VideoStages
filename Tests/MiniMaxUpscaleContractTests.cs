using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// MiniMax H3 upscaling: which modes touch the joint latent and which resize decoded frames
/// between stages.
/// </summary>
[Collection("VideoStagesTests")]
public class MiniMaxUpscaleContractTests
{
    /// <summary>Nothing resolves or downloads this; the loader only names it.</summary>
    private const string UpscaleModelFileName = "unit-test-upscaler.pth";

    /// <summary>
    /// A mis-dispatch is invisible at plan level and the frontend offers <c>latentmodel-*</c> by
    /// default; the danger is a pixel upscale silently substituted.
    /// </summary>
    [Fact]
    public async Task Latent_model_upscale_emits_no_upscale_nodes()
    {
        using MiniMaxWorkflowFixture fixture = MiniMaxWorkflowFixture.Create();
        JObject clip = MakeClip(
            fixture.Stage(),
            fixture.Stage(
                "PreviousStage",
                control: 0,
                upscale: 1.5,
                upscaleMethod: Fixtures.LtxV23SpatialUpscaler));
        clip["duration"] = 1.0;

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Empty(bridge.Graph.NodesOfType<UpscaleModelLoaderNode>());
        Assert.Empty(bridge.Graph.NodesOfType<ImageUpscaleWithModelNode>());

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        EmptyMiniMaxH3LatentAVNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyMiniMaxH3LatentAVNode>());
        Assert.Equal(MiniMaxWorkflowFixture.Width, latent.Width.LiteralAsInt());
        Assert.Equal(MiniMaxWorkflowFixture.Height, latent.Height.LiteralAsInt());

        live.AssertAllLive(sampler, latent);
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// Core's post-cleanup deletes a <c>start_at_step &gt;= steps</c> SwarmKSampler and rewires past
    /// it; the upscale must survive that rewire on the live path.
    /// </summary>
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
        live.AssertNoOrphanNodes();
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// A pixel upscale scales the previous stage's decoded frames, so the next stage's joint latent
    /// is re-encoded from the scaled image rather than from a scaled latent.
    /// </summary>
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

        // Nothing is scaled in latent space; the latent sibling above is the positive control.
        Assert.Empty(bridge.Graph.NodesOfType<LatentUpscaleByNode>());

        live.AssertAllLive(first, second, scale);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Latent interpolation scales the video half in latent space and leaves the audio half — which
    /// shares the same joint latent — untouched. The zero-control sibling only proves the node
    /// survives core's sampler deletion; this proves a refining stage samples the scaled latent.
    /// </summary>
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
        // 2.0, not the authored-elsewhere 1.5: LatentUpscaleBy's generated default IS 1.5.
        Assert.Equal(2.0, scale.ScaleBy.LiteralAsDouble());
        Assert.True(ReachesUpstream(bridge, scale, first.Id));

        LTXVConcatAVLatentNode joint = JointLatentOf(second);
        Assert.Same(scale.LATENT, joint.VideoLatent.Connection);
        Assert.NotSame(scale.LATENT, joint.AudioLatent.Connection);

        // No pixel round trip: nothing is scaled to the upscaled edge in image space.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 1024);
        Assert.NotNull(live.PublishedAudio());

        live.AssertAllLive(first, second, scale);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A model upscale runs an ESRGAN-style model over the decoded frames and then fits the result
    /// to the stage resolution, because the model's own factor is fixed and need not match the
    /// authored one.
    /// </summary>
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
