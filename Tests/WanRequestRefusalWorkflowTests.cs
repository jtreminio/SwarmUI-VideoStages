using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Request-global settings and authored values Wan cannot honour: what is refused outright and what
/// is warned about and dropped.
/// </summary>
[Collection("VideoStagesTests")]
public class WanRequestRefusalWorkflowTests
{
    /// <summary>
    /// A later stage's Control is a fraction of its own step count, and 0.9 over 8 steps floors to
    /// start step 0 — a refining pass that would silently regenerate everything. The request is
    /// refused readably, before any VideoStages phase touches the graph.
    /// <para>
    /// Neither stage authors an image reference, so this also pins the defaults that make stage 1 a
    /// refining pass at all: <c>Generated</c> first, <c>PreviousStage</c> after.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_later_control_that_quantizes_to_start_step_zero_is_refused()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();
        JObject first = fixture.Stage(steps: 8);
        JObject second = fixture.Stage(control: 0.9, steps: 8);
        first.Remove("imageReference");
        second.Remove("imageReference");
        WorkflowGenerator captured = null;
        JObject beforePreflight = null;
        TimelineSpec parsed = null;

        SwarmReadableErrorException error = await Assert.ThrowsAsync<SwarmReadableErrorException>(
            () => ComfyWorkflowApiTestHarness.GenerateAsync(
                fixture.ImageToVideoPost(MakeDocument(MakeClip(first, second))),
                extraSteps:
                [
                    new(g =>
                    {
                        captured = g;
                        beforePreflight = (JObject)g.Workflow.DeepClone();
                        parsed = g.GetTimelineSpec();
                    }, Constants.WorkflowStepPriority.PreflightRequest - 0.1),
                ]));

        Assert.Contains("quantizes to sampler start step 0", error.Message);
        Assert.Equal("Generated", parsed.Clips[0].Stages[0].ImageReference);
        Assert.Equal("PreviousStage", parsed.Clips[0].Stages[1].ImageReference);
        Assert.True(
            JToken.DeepEquals(beforePreflight, captured.Workflow),
            "A VideoStages phase mutated the graph before preflight refused the request.");
    }

    // ---- request-global settings the timeline refuses -----------------------------------

    /// <summary>
    /// The request's global end image only has a home when exactly one clip ends the timeline on a
    /// profile that takes an end frame. Every other shape warns and drops it rather than guessing:
    /// more than one clip has no single last frame; the 5B profile has no first/last conditioning
    /// node at all; a text-to-video entry has no conditioning to attach it to; and a source clip's
    /// ending is the footage's.
    /// <para>
    /// The end image is left on the request in every case — it is ignored, not consumed — and
    /// <see cref="WanFrameReferenceWorkflowTests.The_global_end_image_belongs_to_the_clips_terminal_generating_stage"/> is the
    /// control that it does reach the graph when it can.
    /// </para>
    /// <para>
    /// Entry mode is checked before the model, so the two text-to-video arms are refused for that
    /// reason alone; what separates them is the entry latent each profile builds instead, which the
    /// dropped end image must leave untouched.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, "two-clips")]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, "image-to-video")]
    [InlineData(WanWorkflowFixture.Wan22Ti2v5bFixturePath, "text-to-video")]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, "text-to-video")]
    [InlineData(WanWorkflowFixture.Wan22I2v14bFixturePath, "source-clip")]
    public async Task An_unusable_global_end_image_warns_and_is_dropped(
        string modelFixturePath,
        string shape)
    {
        bool textToVideo = shape == "text-to-video";
        using WanWorkflowFixture fixture = textToVideo
            ? WanWorkflowFixture.Create(modelFixturePath)
            : WanWorkflowFixture.CreateWithBaseModel(modelFixturePath);
        JObject document = shape switch
        {
            "two-clips" => MakeDocument(
                MakeClip(fixture.Stage(steps: 10)), MakeClip(fixture.Stage(steps: 10))),
            "source-clip" => MakeDocument(WanWorkflowFixture.SourceClip(fixture.Stage(control: 1, steps: 10))),
            _ => MakeDocument(MakeClip(fixture.Stage(control: 1, steps: 10))),
        };
        void Customize(JObject post) => post["videoendimage"] = WanWorkflowFixture.EndImagePayload;
        Image endFrameBeforeStages = null;

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                textToVideo
                    ? fixture.Post(document, Customize)
                    : fixture.ImageToVideoPost(document, Customize),
                extraSteps:
                [
                    new(g => endFrameBeforeStages =
                            g.UserInput.Get(T2IParamTypes.VideoEndImage, null),
                        Constants.WorkflowStepPriority.RunConfiguredStages - 0.01),
                ]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Session-provider preflight diagnostics land on the context, not on the plan.
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().PreflightDiagnostics,
            diagnostic => diagnostic.Code == "wan.end-frame.ignored");
        Assert.Empty(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());
        // The arm really ran on the checkpoint it names, and the profile still builds its own
        // entry: 5B its native latent, 14B the conditioning shapes that node never appears in.
        SwarmKSamplerNode first = StageSampler(bridge, 0);
        Assert.Equal(
            Path.GetFileName(modelFixturePath),
            ModelBranchOf(first).Loader.UnetName.LiteralAsString());
        Assert.Equal(
            modelFixturePath == WanWorkflowFixture.Wan22Ti2v5bFixturePath ? 1 : 0,
            bridge.Graph.NodesOfType<Wan22ImageToVideoLatentNode>().Count);
        // The same instance, not merely some image: "ignored" means untouched, and a path that
        // swapped in a replacement would satisfy a non-null check.
        Assert.NotNull(endFrameBeforeStages);
        Assert.Same(
            endFrameBeforeStages,
            generator.UserInput.Get(T2IParamTypes.VideoEndImage, null));

        live.AssertAllLive(first);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A timeline whose clips are on different architectures has no single WAN clip to end, so the
    /// end image is dropped and the warning names both families — the arm that separates this from
    /// the plain two-clip refusal, where every clip is WAN.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_mixed_timeline_drops_the_global_end_image_and_names_both_families(
        bool wanFirst)
    {
        using MultiModelFixture fixture = MultiModelFixture.Create(
            Ltx2WorkflowFixture.ModelFixturePath,
            WanWorkflowFixture.Wan22Ti2v5bFixturePath);
        JObject ltxClip = MakeClip(MakeStage(fixture.Model.Name, "Generated", steps: 7));
        JObject wanClip = MakeClip(MakeStage(fixture.Models[1].Name, "Generated", steps: 9));

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(
                    wanFirst
                        ? MakeDocument(wanClip, ltxClip)
                        : MakeDocument(ltxClip, wanClip),
                    post => post["videoendimage"] = WanWorkflowFixture.EndImagePayload));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // Session-provider preflight diagnostics land on the context, not on the plan.
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().PreflightDiagnostics,
            diagnostic => diagnostic.Code == "wan.end-frame.ignored");
        Assert.Equal(
            wanFirst ? ["wan22", "ltx2"] : (string[])["ltx2", "wan22"],
            generator.RequireVideoExecutionPlanContext().Plan.Clips
                .Select(clip => clip.Architecture.Id.Value)
                .ToArray());
        Assert.Empty(bridge.Graph.NodesOfType<WanFirstLastFrameToVideoNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVAddGuideNode>());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadImageB64Node>());

        // Both clips still generated; only the end image was refused.
        live.AssertAllLive(StageSampler(bridge, 0), StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// SwarmUI's request-global Video2Video Creativity would start the diffusion late; WAN stages
    /// carry their own <c>Control</c>, so the global value is warned about and ignored — the stage
    /// starts at step 0 as its own <c>control: 1</c> asks, not at the step 0.5 creativity implies.
    /// </summary>
    [Fact]
    public async Task A_global_creativity_warns_and_the_stages_own_control_decides()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(
                    MakeDocument(MakeClip(fixture.Stage(control: 1, steps: 10))),
                    post => post["video2videocreativity"] = 0.5));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Equal(10, stage.Steps.LiteralAsInt());
        Assert.Equal(0, stage.StartAtStep.LiteralAsInt());
        // Session-provider preflight diagnostics land on the context, not on the plan.
        Assert.Contains(
            generator.RequireVideoExecutionPlanContext().PreflightDiagnostics,
            diagnostic => diagnostic.Code == "wan22.host-param.unsupported");

        live.AssertAllLive(stage);
        AssertShippable(bridge, workflow, live);
    }
}
