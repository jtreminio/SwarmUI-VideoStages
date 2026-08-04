using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Request-global frame interpolation over a VideoStages timeline, generated through the real Comfy
/// API POST path on WAN image-to-video.
/// <para>
/// The POST shape matters twice over. Core runs an interpolation branch of its own at priority 10
/// and again at 11; both are inside the video root the extension prunes at 11.05, so a graph
/// carrying more than one interpolation node means core's copy survived alongside the timeline's.
/// And core's base-image save (node <c>30</c>, emitted because the request has a followup video)
/// belongs to core, not the timeline — it is the reason a <c>donotsave</c> request still ships one
/// output node.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public sealed class FrameInterpolationContractTests
{
    /// <summary>Frames WAN generates for this fixture's request, before any trim.</summary>
    private const int GeneratedFrames = 25;

    private static JObject InterpolationPost(
        WanWorkflowFixture fixture,
        string method,
        int multiplier,
        Action<JObject> customize = null) =>
        fixture.ImageToVideoPost(
            MakeDocument(MakeClip(fixture.Stage(control: 0.5, steps: 8))),
            post =>
            {
                post["videoframeinterpolationmethod"] = method;
                post["videoframeinterpolationmultiplier"] = multiplier;
                customize?.Invoke(post);
            });

    /// <summary>No generated ComfyTyped class exists for either GIMM-VFI node.</summary>
    private const string GimmInterpolateClass = "GIMMVFI_interpolate";

    private const string GimmLoaderClass = "DownloadAndLoadGIMMVFIModel";

    /// <summary>Every interpolation node class, whichever method built it.</summary>
    private static IReadOnlyList<ComfyNode> InterpolationNodes(WorkflowBridge bridge) =>
        [.. bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName is
            RIFEVFINode.ClassType or FILMVFINode.ClassType or GimmInterpolateClass)];

    /// <summary>
    /// The decode the timeline hands on, named by the sampler it decodes rather than by node class:
    /// WAN can decode tiled, and core's post-cleanup rewrites decode chains.
    /// </summary>
    private static void AssertDecodesTheStage(ComfyNode source, SwarmKSamplerNode stage) =>
        Assert.Same(stage, source?.FindInput("samples")?.Connection?.Node);

    // ---- the interpolation node itself --------------------------------------------------

    /// <summary>
    /// One interpolation node, fed by the trim, publishing at the multiplied rate — with the
    /// untouched trim published alongside it as the intermediate output. Core builds interpolation
    /// of its own twice on the way here, so <c>Assert.Single</c> is the load-bearing claim.
    /// </summary>
    [Fact]
    public async Task Rife_interpolates_the_final_trim_once_before_publication()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                InterpolationPost(fixture, "RIFE", 2, post =>
                {
                    post["trimvideostartframes"] = 4;
                    post["outputintermediateimages"] = true;
                }),
                ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        RIFEVFINode rife = Assert.Single(bridge.Graph.NodesOfType<RIFEVFINode>());
        Assert.Equal(2, rife.Multiplier.LiteralAsInt());
        SwarmTrimFramesNode trim = Assert.IsType<SwarmTrimFramesNode>(rife.Frames.Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        AssertDecodesTheStage(trim.Image.Connection?.Node, StageSampler(bridge, 0));

        SwarmSaveAnimationWSNode[] saves =
            [.. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()];
        SwarmSaveAnimationWSNode intermediate = Assert.Single(
            saves,
            save => ReferenceEquals(trim, save.Images.Connection?.Node));
        Assert.Equal(24.0, intermediate.Fps.LiteralAsDouble());
        SwarmSaveAnimationWSNode published = Assert.Single(
            saves,
            save => ReferenceEquals(rife, save.Images.Connection?.Node));
        Assert.Equal(48.0, published.Fps.LiteralAsDouble());

        // 25 generated frames, 4 trimmed, then endpoint arithmetic: (21 - 1) * 2 + 1.
        Assert.Equal(41, generator.CurrentMedia.Frames);
        Assert.Equal(48, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.Null(generator.CurrentMedia.Compat);
        Assert.Equal(rife.Id, $"{generator.CurrentMedia.Path[0]}");
        Assert.NotNull(bridge.ResolvePath(generator.CurrentMedia.Path));

        // The intermediate save is an output in its own right, so it is deliberately not asserted
        // to reach the published one.
        live.AssertAllLive(trim, rife, published);
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    /// <summary>
    /// <c>donotsave</c> suppresses both video outputs the previous test pins — the intermediate
    /// pre-interpolation save and the publication — while the interpolation chain is still built.
    /// <para>
    /// The graph is not shippable and cannot be: with nothing publishing the timeline, every node
    /// it built is orphaned by construction, so the orphan set is pinned instead. Nor is it output
    /// free — core's own base-image save survives, which is why
    /// <see cref="WorkflowLivePath.AssertNoPublishedOutput"/> does not apply to this shape.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Do_not_save_suppresses_both_outputs_without_suppressing_interpolation()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                InterpolationPost(fixture, "RIFE", 2, post =>
                {
                    post["trimvideostartframes"] = 4;
                    post["outputintermediateimages"] = true;
                    post["donotsave"] = true;
                }),
                ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        // Core's base-image save is not the timeline's to suppress, and it still ships.
        Assert.Single(bridge.Graph.NodesOfType<SwarmSaveImageWSNode>());

        RIFEVFINode rife = Assert.Single(bridge.Graph.NodesOfType<RIFEVFINode>());
        SwarmTrimFramesNode trim = Assert.IsType<SwarmTrimFramesNode>(rife.Frames.Connection?.Node);
        Assert.Equal(4, trim.TrimStart.LiteralAsInt());
        Assert.Equal(41, generator.CurrentMedia.Frames);
        Assert.Equal(48, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(rife.Id, $"{generator.CurrentMedia.Path[0]}");

        Assert.Contains($"{RIFEVFINode.ClassType}#{rife.Id}", live.OrphanNodes());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    /// <summary>
    /// FILM replaces RIFE outright — one interpolation node, its own class, and the same endpoint
    /// arithmetic at a multiplier of 3.
    /// </summary>
    [Fact]
    public async Task Film_routes_the_final_video_and_uses_endpoint_frame_arithmetic()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                InterpolationPost(fixture, "FILM", 3),
                ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        FILMVFINode film = Assert.Single(bridge.Graph.NodesOfType<FILMVFINode>());
        Assert.Equal(3, film.Multiplier.LiteralAsInt());
        Assert.Same(film, Assert.Single(InterpolationNodes(bridge)));
        AssertDecodesTheStage(film.Frames.Connection?.Node, StageSampler(bridge, 0));

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Same(film, published.Images.Connection?.Node);
        Assert.Equal(72.0, published.Fps.LiteralAsDouble());

        // (25 - 1) * 3 + 1, at three times the timeline's 24 fps.
        Assert.Equal(73, generator.CurrentMedia.Frames);
        Assert.Equal(72, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(film.Id, $"{generator.CurrentMedia.Path[0]}");

        live.AssertAllLive(film, published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// GIMM-VFI is the only method that loads a model of its own, so its interpolate node has a
    /// second live input the other two do not.
    /// </summary>
    [Fact]
    public async Task Gimm_routes_through_its_model_loader_and_uses_endpoint_frame_arithmetic()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                InterpolationPost(fixture, "GIMM-VFI", 3),
                ["frameinterps", "frameinterps_gimmvfi"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        ComfyNode loader = Assert.Single(bridge.Graph.NodesOfType(GimmLoaderClass));
        ComfyNode gimm = Assert.Single(bridge.Graph.NodesOfType(GimmInterpolateClass));
        Assert.Same(gimm, Assert.Single(InterpolationNodes(bridge)));
        Assert.Same(loader, gimm.FindInput("gimmvfi_model").Connection?.Node);
        Assert.Equal(3, gimm.FindInput("interpolation_factor").LiteralAsInt());
        AssertDecodesTheStage(gimm.FindInput("images").Connection?.Node, StageSampler(bridge, 0));

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Same(gimm, published.Images.Connection?.Node);
        Assert.Equal(72.0, published.Fps.LiteralAsDouble());
        Assert.Equal(73, generator.CurrentMedia.Frames);
        Assert.Equal(72, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(gimm.Id, $"{generator.CurrentMedia.Path[0]}");

        live.AssertAllLive(loader, gimm, published);
        AssertShippable(bridge, workflow, live);
    }

    // ---- when interpolation must not happen ----------------------------------------------

    /// <summary>
    /// A multiplier of 1 is the param's <c>IgnoreIf</c> value, so the POST carries no interpolation
    /// setting at all — interpolation is skipped for want of a request, not by the
    /// <c>multiplier == 1</c> guard, which no caller can reach. The named method survives the trip
    /// and is still ignored.
    /// </summary>
    [Fact]
    public async Task Multiplier_one_leaves_the_request_without_an_interpolation_setting()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                InterpolationPost(fixture, "RIFE", 1),
                ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.False(generator.UserInput.TryGet(
            ComfyUIBackendExtension.VideoFrameInterpolationMultiplier,
            out _));
        Assert.True(generator.UserInput.TryGet(
            ComfyUIBackendExtension.VideoFrameInterpolationMethod,
            out _));
        Assert.Empty(InterpolationNodes(bridge));

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Equal(24.0, published.Fps.LiteralAsDouble());
        AssertDecodesTheStage(published.Images.Connection?.Node, StageSampler(bridge, 0));
        Assert.Equal(GeneratedFrames, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertLive(published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A one-frame timeline has no interval to interpolate across, so a fully configured request is
    /// a no-op: no interpolation node, the unmultiplied frame rate, and — because the intermediate
    /// save only exists to preserve the pre-interpolation video — a single output.
    /// </summary>
    [Fact]
    public async Task One_frame_timeline_does_not_change_the_final_save_rate()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                InterpolationPost(fixture, "RIFE", 2, post =>
                {
                    post["videoframes"] = 1;
                    post["outputintermediateimages"] = true;
                }),
                ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.True(generator.UserInput.TryGet(
            ComfyUIBackendExtension.VideoFrameInterpolationMultiplier,
            out int multiplier));
        Assert.Equal(2, multiplier);
        Assert.Empty(InterpolationNodes(bridge));

        SwarmSaveAnimationWSNode published = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(24.0, published.Fps.LiteralAsDouble());
        Assert.Equal(1, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertLive(published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Interpolation is request-global, so a timeline of more than one clip would synthesize frames
    /// across an authored boundary. The request still generates; it just warns and builds no
    /// interpolation node.
    /// </summary>
    [Fact]
    public async Task Multi_clip_timeline_warns_and_skips_interpolation()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(
                        MakeClip(1.0, fixture.Stage(control: 0.5, steps: 8)),
                        MakeClip(1.0, fixture.Stage(control: 0.5, steps: 8))),
                    post =>
                    {
                        post["videoframeinterpolationmethod"] = "RIFE";
                        post["videoframeinterpolationmultiplier"] = 2;
                    }),
                ["frameinterps"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(InterpolationNodes(bridge));
        // Both clips really did generate; interpolation alone was refused.
        live.AssertAllLive(StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertWarnsAndSkips(generator, "2 clips joined by 1 boundary");

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Equal(24.0, published.Fps.LiteralAsDouble());
        live.AssertLive(published);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Each method names the backend features its nodes come from. Missing any of them warns and
    /// skips rather than shipping a workflow ComfyUI cannot execute. GIMM-VFI needs two, so it
    /// appears twice: once missing its own feature, once missing the common one.
    /// </summary>
    [Theory]
    [InlineData("RIFE", new string[0], "frameinterps")]
    [InlineData("FILM", new string[0], "frameinterps")]
    [InlineData("GIMM-VFI", new[] { "frameinterps" }, "frameinterps_gimmvfi")]
    [InlineData("GIMM-VFI", new[] { "frameinterps_gimmvfi" }, "frameinterps")]
    public async Task Missing_method_feature_warns_and_skips_interpolation(
        string method,
        string[] installedFeatures,
        string missingFeature)
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                InterpolationPost(fixture, method, 2),
                installedFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Empty(InterpolationNodes(bridge));
        AssertWarnsAndSkips(generator, $"'{missingFeature}'");

        SwarmSaveAnimationWSNode published = live.FinalVideoSave();
        Assert.Equal(24.0, published.Fps.LiteralAsDouble());
        AssertDecodesTheStage(published.Images.Connection?.Node, StageSampler(bridge, 0));
        Assert.Equal(GeneratedFrames, generator.CurrentMedia.Frames);
        Assert.Equal(24, generator.CurrentMedia.GetRawFPS());

        live.AssertLive(published);
        AssertShippable(bridge, workflow, live);
    }

    private static void AssertWarnsAndSkips(WorkflowGenerator generator, string expected)
    {
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains(expected, StringComparison.Ordinal)
                && warning.Contains("will be skipped", StringComparison.Ordinal));
    }
}
