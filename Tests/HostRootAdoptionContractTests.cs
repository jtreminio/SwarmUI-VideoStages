using ComfyTyped.Core;
using ComfyTyped.Families;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// What a timeline stage adopts from SwarmUI's own root chain instead of rebuilding. Reuse is what
/// keeps the root cleanup from having anything to delete: <c>RemoveOwnedNodesNotLive</c> is
/// liveness-based, so any core node a stage consumes survives it.
/// <para>
/// The model loader is adopted at <see cref="WorkflowGenerator.CreateNode"/>'s dedup cache, not at
/// <c>CreateModelLoader</c>'s <c>modelloader_*</c> cache — the extension clears that key per stage
/// so a stage's LoRAs and model patches still apply. Because dedup happens after the model-gen
/// steps run, the reused node is the tail they had nothing to add to, which is why adoption cannot
/// silently drop a stage-scoped patch.
/// </para>
/// </summary>
[Collection("VideoStagesTests")]
public sealed class HostRootAdoptionContractTests
{
    private const string StageLoraName = "UnitTest_AdoptionStageLora";

    private static VideoStagesWorkflowFixture CreateFixture(string architecture) =>
        architecture switch
        {
            "ltx2" => Ltx2WorkflowFixture.Create(),
            "wan" => WanWorkflowFixture.Create(),
            "minimax" => MiniMaxWorkflowFixture.Create(),
            "host-video" => MochiWorkflowFixture.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
        };

    /// <summary>
    /// Core's reserved id for the base model loader, per <c>WorkflowGeneratorSteps</c>'s id table.
    /// Asserting the id — not merely that one loader exists — is what distinguishes adoption from
    /// the extension building its own loader and the cleanup deleting core's.
    /// </summary>
    private const string CoreBaseLoaderId = "4";

    private const string CoreSamplerId = "10";

    private const string CoreDecodeId = "8";

    private const string CoreLatentId = "5";

    private static UNETLoaderNode AssertSingleCoreBaseLoader(WorkflowBridge bridge)
    {
        UNETLoaderNode loader = Assert.Single(bridge.Graph.NodesOfType<UNETLoaderNode>());
        Assert.Equal(CoreBaseLoaderId, loader.Id);
        return loader;
    }

    /// <summary>
    /// Every architecture's text-to-video stage loads the request's model through core's own base
    /// loader. A second loader would be a second copy of the same weights on the GPU, and would
    /// leave core's node dead for the root cleanup to sweep.
    /// </summary>
    [Theory]
    [InlineData("ltx2")]
    [InlineData("wan")]
    [InlineData("minimax")]
    [InlineData("host-video")]
    public async Task Text_to_video_stages_load_the_model_through_cores_base_loader(
        string architecture)
    {
        using VideoStagesWorkflowFixture fixture = CreateFixture(architecture);

        JObject workflow = await fixture.GenerateAsync(MakeDocument(MakeClip(
            1.0,
            fixture.Stage(),
            fixture.Stage(control: 0.5))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        UNETLoaderNode loader = AssertSingleCoreBaseLoader(bridge);
        for (int stageId = 0; stageId < 2; stageId++)
        {
            Assert.True(
                ReachesUpstream(bridge, StageSampler(bridge, stageId), CoreBaseLoaderId),
                $"Stage {stageId} does not sample through core's base loader.");
        }

        live.AssertLive(loader);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The case the <c>modelloader_*</c> cache alone would get wrong: a stage LoRA must still be
    /// applied, and it is — as a loader chained onto core's node, not as a reload of the model.
    /// The unmodified second stage keeps sampling straight off core's node.
    /// </summary>
    [Fact]
    public async Task A_stage_lora_chains_onto_cores_base_loader_rather_than_reloading_the_model()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", $"{StageLoraName}.safetensors");
        JObject loraStage = fixture.Stage();
        loraStage["loras"] = new JArray(new JObject
        {
            ["name"] = StageLoraName,
            ["weight"] = 1.0,
        });

        JObject workflow = await fixture.GenerateAsync(MakeDocument(MakeClip(
            1.0,
            loraStage,
            fixture.Stage(control: 0.5))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        UNETLoaderNode loader = AssertSingleCoreBaseLoader(bridge);
        LoraLoaderNode lora = Assert.Single(bridge.Graph.NodesOfType<LoraLoaderNode>());
        Assert.Same(loader, lora.Model.Connection?.Node);
        Assert.Same(lora, StageSampler(bridge, 0).Model.Connection?.Node);
        Assert.Same(loader, StageSampler(bridge, 1).Model.Connection?.Node);

        live.AssertAllLive(loader, lora);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A single-stage text-to-video timeline ships core's own graph shape with the stage's
    /// settings in it: loader, empty latent and conditioning adopted by dedup, sampler and decode
    /// claimed by id, core's save reading the claimed decode. The root cleanup has nothing left to
    /// remove — every node core built is one the stage is using.
    /// </summary>
    [Theory]
    [InlineData("wan")]
    [InlineData("host-video")]
    [InlineData("minimax")]
    [InlineData("ltx2")]
    public async Task A_text_stage_claims_cores_sampler_and_decode(string architecture)
    {
        using VideoStagesWorkflowFixture fixture = CreateFixture(architecture);

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(MakeClip(1.0, fixture.Stage())));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(CoreSamplerId, sampler.Id);
        Assert.Same(sampler, StageSampler(bridge, 0));
        // Each family names its empty latent differently, so the id is the common ground.
        Assert.True(
            ReachesUpstream(bridge, sampler, CoreLatentId),
            "The stage does not sample core's own empty latent.");

        // By interface, not by node class: LTX-2 decodes tiled and H3 splits its joint latent
        // first, so neither the class nor a direct connection to the sampler is common ground.
        ComfyNode decode = Assert.IsAssignableFrom<ComfyNode>(
            Assert.Single(bridge.Graph.Nodes.Values.OfType<IVaeDecode>()));
        Assert.Equal(CoreDecodeId, decode.Id);
        Assert.True(ReachesUpstream(bridge, decode, sampler.Id));
        Assert.Same(decode, live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive(sampler, decode);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The conditioning pair, on core's terms: its id map reserves 6 for the positive prompt and 7
    /// for the negative, and a stage that takes those ids has to honour that or the encoders swap
    /// roles for anything downstream that reads them by id.
    /// </summary>
    [Fact]
    public async Task An_ltx_text_stage_claims_cores_conditioning_pair_in_cores_own_roles()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(MakeClip(1.0, fixture.Stage())));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmClipTextEncodeAdvancedNode positive =
            RequireTypedNode<SwarmClipTextEncodeAdvancedNode>(bridge, "6");
        SwarmClipTextEncodeAdvancedNode negative =
            RequireTypedNode<SwarmClipTextEncodeAdvancedNode>(bridge, "7");
        Assert.Equal(
            "A red kite flying over a green field",
            positive.Prompt.LiteralAsString());
        Assert.Equal("", negative.Prompt.LiteralAsString());

        LTXVConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConditioningNode>());
        Assert.Same(positive, conditioning.PositiveInput.Connection?.Node);
        Assert.Same(negative, conditioning.NegativeInput.Connection?.Node);
        Assert.Same(conditioning, StageSampler(bridge, 0).Positive.Connection?.Node);

        live.AssertAllLive(positive, negative, conditioning);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// LTX-2 builds its latent through the typed bridge, which never consults the dedup cache the
    /// other families' latents collapse through, so it takes core's id outright. What sits above it
    /// then collapses unaided: with core's video and audio latents on both inputs, the stage's joint
    /// latent is byte-identical to core's and dedups onto it.
    /// </summary>
    [Fact]
    public async Task An_ltx_text_stage_claims_cores_latent_and_its_joint_latent_follows()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(MakeClip(1.0, fixture.Stage())));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        EmptyLTXVLatentVideoNode latent = Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>());
        Assert.Equal(CoreLatentId, latent.Id);

        LTXVConcatAVLatentNode joint = Assert.Single(
            bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>());
        Assert.Same(latent, joint.VideoLatent.Connection?.Node);
        Assert.Same(joint, StageSampler(bridge, 0).LatentImage.Connection?.Node);
        // Core allocated the concat dynamically rather than at a reserved id, so it is only ever
        // adopted by collapse — nothing claims it.
        Assert.True(int.Parse(joint.Id) < Constants.StagedNodeIdReservationFloor);

        live.AssertAllLive(latent, joint);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Claiming core's latent has to retire its dedup entry, or the next family's latent collapses
    /// onto the claiming family's. The collision needs the request's own model to be the second
    /// family's: core then builds that family's latent at the reserved id, and a clip of the same
    /// length asks for a byte-identical one — the dedup key of the node LTX-2 has already taken
    /// over.
    /// </summary>
    [Fact]
    public async Task Claiming_cores_latent_retires_the_dedup_entry_another_family_would_hit()
    {
        using LtxAndWanFixture fixture = new();

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(
                MakeClip(1.0, fixture.Stage()),
                MakeClip(1.0, Fixtures.MakeStage(fixture.SecondModel.Name))),
            post => post["model"] = fixture.SecondModel.Name);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(CoreLatentId, Assert.Single(
            bridge.Graph.NodesOfType<EmptyLTXVLatentVideoNode>()).Id);
        Assert.False(
            ReachesUpstream(bridge, StageSampler(bridge, 1), CoreLatentId),
            "The WAN clip samples the latent the LTX-2 clip claimed.");

        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// There is one host root, so only one stage may take it. The second clip's first stage is a
    /// text stage on the same terms as the first's, and builds its own sampler and decode.
    /// </summary>
    [Fact]
    public async Task Only_the_first_generated_stage_claims_the_host_root()
    {
        using MochiWorkflowFixture fixture = MochiWorkflowFixture.Create();

        JObject workflow = await fixture.GenerateAsync(MakeDocument(
            MakeClip(1.0, fixture.Stage()),
            MakeClip(1.0, fixture.Stage())));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode first = StageSampler(bridge, 0);
        SwarmKSamplerNode second = StageSampler(bridge, 1);
        Assert.Equal(CoreSamplerId, first.Id);
        Assert.NotEqual(CoreSamplerId, second.Id);
        Assert.NotEqual(CoreDecodeId, Assert.Single(
            bridge.Graph.NodesOfType<VAEDecodeNode>(),
            node => node.Samples.Connection?.Node == second).Id);

        live.AssertAllLive(first, second);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// Image-to-video keeps core's sampler and decode for the base image the video drives from, so
    /// the video stage must leave both alone. Claiming there would overwrite the image the stage
    /// itself is about to consume.
    /// </summary>
    [Fact]
    public async Task An_image_to_video_stage_leaves_cores_sampler_and_decode_alone()
    {
        using Hunyuan15WorkflowFixture fixture = Hunyuan15WorkflowFixture.CreateWithBaseModel();

        JObject workflow = await fixture.GenerateImageToVideoAsync(
            MakeDocument(MakeClip(1.0, fixture.Stage())));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode baseSampler = fixture.BaseSampler(bridge);
        Assert.Equal(CoreSamplerId, baseSampler.Id);
        Assert.NotEqual(CoreSamplerId, StageSampler(bridge, 0).Id);

        live.AssertAllLive(baseSampler, StageSampler(bridge, 0));
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// A keyframe reference sourced from the host's own pass is captured as a node id, not as a
    /// graph edge, so nothing about the graph at claim time says the node is spoken for. Claiming
    /// it anyway would hand the reference the claiming stage's output under the name of the host's,
    /// silently — so a captured host reference refuses the claim outright.
    /// </summary>
    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public async Task A_captured_host_reference_refuses_the_claim(string source)
    {
        using WanAndMiniMaxFixture fixture = new();
        JObject miniMaxClip = MakeClipWithRefs(
            [MakeRef(source)],
            Fixtures.MakeStage(fixture.SecondModel.Name, steps: MiniMaxWorkflowFixture.Steps,
                cfgScale: MiniMaxWorkflowFixture.CfgScale));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(
            MakeClip(1.0, fixture.Stage()),
            miniMaxClip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode wanStage = StageSampler(bridge, 0);
        Assert.NotEqual(CoreSamplerId, wanStage.Id);
        SwarmMiniMaxH3AddKeyframesNode keyframes = Assert.Single(
            bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());
        Assert.False(
            ReachesUpstream(bridge, keyframes.FirstFrame.Connection?.Node, wanStage.Id),
            $"The '{source}' keyframe resolves to the WAN clip's own generation.");

        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The claim belongs to the timeline rather than to an architecture: whichever clip generates
    /// first takes it, and the other family's clip neither takes it nor blocks it. LTX-2 used to
    /// capture the host root unconditionally, and since a capture refuses the claim, one LTX-2 clip
    /// denied it to the whole timeline whichever end it sat at.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_first_generated_stage_claims_whichever_family_owns_it(bool ltxLeads)
    {
        using LtxAndWanFixture fixture = new();
        JObject ltxClip = MakeClip(0.6, fixture.Stage());
        JObject wanClip = MakeClip(0.6, Fixtures.MakeStage(fixture.SecondModel.Name));

        JObject workflow = await fixture.GenerateAsync(MakeDocument(
            ltxLeads ? ltxClip : wanClip,
            ltxLeads ? wanClip : ltxClip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        SwarmKSamplerNode claimed = RequireTypedNode<SwarmKSamplerNode>(bridge, CoreSamplerId);
        Assert.Same(claimed, StageSampler(bridge, 0));
        Assert.NotEqual(CoreSamplerId, StageSampler(bridge, 1).Id);
        Assert.Equal(
            CoreDecodeId,
            Assert.Single(
                bridge.Graph.Nodes.Values.OfType<IVaeDecode>().Cast<ComfyNode>(),
                decode => ReachesUpstream(bridge, decode, claimed.Id)).Id);

        live.AssertLive(claimed);
        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// An authored reference to the host's base pass costs the timeline nothing on a text-to-video
    /// request, because there is no host pass to reference: the spec parser rewrites every stage's
    /// ImageReference to <c>Generated</c>, and <c>LtxClipRefResolver</c> drops every non-upload
    /// clip ref. Refusing the claim for it would trade a whole extra root pass for a reference that
    /// is discarded either way.
    /// </summary>
    [Fact]
    public async Task An_ltx_base_clip_ref_costs_the_timeline_nothing_on_text_to_video()
    {
        using LtxAndWanFixture fixture = new();

        JObject workflow = await fixture.GenerateAsync(MakeDocument(
            MakeClipWithRefs([MakeRef("Base")], fixture.Stage()),
            MakeClip(0.6, Fixtures.MakeStage(fixture.SecondModel.Name))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(CoreSamplerId, StageSampler(bridge, 0).Id);

        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// The timeline replaces the host's text root, so there is deliberately no host generation for
    /// a stage to reference — and every stage's ImageReference is rewritten to <c>Generated</c> on
    /// such a request. Reporting that as a missing reference would put a warning on the browser for
    /// every text-to-video run.
    /// </summary>
    [Fact]
    public async Task A_discarded_text_root_is_not_reported_as_a_missing_generated_reference()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (_, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(
                    MakeClip(1.0, fixture.Stage()),
                    MakeClip(1.0, fixture.Stage()))));

        Assert.DoesNotContain(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("Generated", StringComparison.Ordinal));
    }

    /// <summary>
    /// The capture that refuses the claim is only taken when a clip asked for it. Capturing
    /// unconditionally would pin core's nodes for every H3 request and deny the claim outright —
    /// the same timeline, minus the reference, claims normally.
    /// </summary>
    [Fact]
    public async Task An_unreferenced_host_capture_is_not_taken_and_does_not_refuse_the_claim()
    {
        using WanAndMiniMaxFixture fixture = new();

        JObject workflow = await fixture.GenerateAsync(MakeDocument(
            MakeClip(1.0, fixture.Stage()),
            MakeClip(1.0, Fixtures.MakeStage(
                fixture.SecondModel.Name,
                steps: MiniMaxWorkflowFixture.Steps,
                cfgScale: MiniMaxWorkflowFixture.CfgScale))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(CoreSamplerId, StageSampler(bridge, 0).Id);

        AssertShippable(bridge, workflow, live);
    }

    /// <summary>
    /// <c>outputintermediateimages</c> with frame interpolation gives core's root decode a second
    /// save, one the root cleanup deliberately spares. A node the cleanup spares is a node someone
    /// else still publishes, so claiming it would ship that stage's output twice under two names
    /// and lose the host pass the setting exists to show.
    /// </summary>
    [Fact]
    public async Task An_intermediate_save_on_cores_decode_refuses_the_claim()
    {
        using WanWorkflowFixture fixture = WanWorkflowFixture.Create();

        JObject workflow = await fixture.GenerateAsync(
            MakeDocument(MakeClip(1.0, fixture.Stage(), fixture.Stage(control: 0.5))),
            post =>
            {
                post["outputintermediateimages"] = true;
                post["videoframeinterpolationmethod"] = "RIFE";
                post["videoframeinterpolationmultiplier"] = 2;
            });
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.NotEqual(CoreSamplerId, StageSampler(bridge, 0).Id);
        // Core's own root pass still reaches a save of its own, which is what the setting promised.
        Assert.Contains(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>(),
            save => save.Images.Connection?.Node?.Id == CoreDecodeId);

        // The timeline's own save, plus the two the setting asked for.
        AssertShippable(bridge, workflow, live, publishedVideoSaves: 3);
    }

    /// <summary>
    /// Every host-root node publishing the timeline had to delete, by id and by the class it held
    /// when it died. Empty is the goal and the usual answer: every node core built is one a stage
    /// took over.
    /// </summary>
    private static IReadOnlyDictionary<string, string> SweptHostRootNodes(
        WorkflowGenerator generator) =>
        VideoGraphHelpers.ReadRemovalRecord(generator, Constants.SweptHostRootNodesKey);

    /// <summary>
    /// The cleanup is a safety net now, not a step. For a text-to-video timeline whose stage takes
    /// core's chain over, it deletes nothing at all — the shipped graph is the same graph core
    /// built, running the stage's settings.
    /// </summary>
    [Theory]
    [InlineData("wan")]
    [InlineData("host-video")]
    [InlineData("minimax")]
    public async Task The_root_cleanup_deletes_nothing_from_an_adopted_text_root(
        string architecture)
    {
        using VideoStagesWorkflowFixture fixture = CreateFixture(architecture);

        (_, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip(1.0, fixture.Stage()))));

        Assert.True(
            generator.NodeHelpers.ContainsKey(Constants.SweptHostRootNodesKey),
            "The timeline never recorded what it swept, so an empty sweep proves nothing.");
        Assert.Empty(SweptHostRootNodes(generator));
    }

    /// <summary>
    /// A single-family LTX-2 text root leaves three nodes behind — its conditioning, its A/V split
    /// and its audio decode — and core would have collected all three itself: it allocates them
    /// dynamically, so there is no reserved id for a stage to take, and lists their classes in
    /// <c>AutoCleanupNodeTypes</c>.
    /// <para>
    /// Only for this shape. A mixed timeline sweeps more and not all of it is core-collectable —
    /// WAN beside MiniMax sweeps an <c>EmptyHunyuanLatentVideo</c>, and a MiniMax refine stage a
    /// <c>VAEDecodeAudio</c>, neither of which core lists. The cleanup is a no-op where one family
    /// owns the root and still does real work where two do not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_ltx_text_roots_remaining_sweep_is_all_core_would_have_collected()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.Post(MakeDocument(MakeClip(1.0, fixture.Stage()))));

        IReadOnlyDictionary<string, string> swept = SweptHostRootNodes(generator);
        Assert.All(swept, entry => Assert.False(workflow.ContainsKey(entry.Key)));
        Assert.All(
            swept,
            entry => Assert.Contains(
                entry.Value,
                WorkflowGeneratorSteps.AutoCleanupNodeTypes));
    }

    /// <summary>
    /// Two clips carrying the same prompt encode it once. A text encode is the expensive part of a
    /// stage — LTX-2 runs a 12B text model — so a timeline that repeats a prompt should not pay for
    /// it twice, and a clip that repeats the previous clip's settings should not rebuild what it
    /// already has.
    /// </summary>
    [Fact]
    public async Task Two_clips_with_the_same_prompt_encode_it_once()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();

        JObject workflow = await fixture.GenerateAsync(MakeDocument(
            MakeClip(1.0, fixture.Stage()),
            MakeClip(1.0, fixture.Stage())));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        // The prompt encoders core reserved 6 and 7 for, and nothing beside them.
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmClipTextEncodeAdvancedNode>().Count);
        Assert.Single(bridge.Graph.NodesOfType<LTXVConditioningNode>());
        // Each clip still samples for itself: the seeds differ, so the samplers cannot be one.
        Assert.NotEqual(StageSampler(bridge, 0).Id, StageSampler(bridge, 1).Id);

        AssertShippable(bridge, workflow, live);
    }
}
