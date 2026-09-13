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

// Loader dedup runs after model patches; clearing modelloader_* per stage preserves stage LoRAs.
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

    // Core reserves this loader id in WorkflowGeneratorSteps; count alone cannot prove adoption.
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

    // Reusing modelloader_* would skip the stage's LoRA patch.
    [Fact]
    public async Task A_clip_lora_chains_onto_cores_base_loader_rather_than_reloading_the_model()
    {
        using Ltx2WorkflowFixture fixture = Ltx2WorkflowFixture.Create();
        fixture.InstallModel("LoRA", $"{StageLoraName}.safetensors");
        JObject loraStage = fixture.Stage();
        JObject clip = MakeClip(
            1.0,
            loraStage,
            fixture.Stage(control: 0.5));
        foreach (JObject authoredStage in clip["stages"])
        {
            authoredStage["loras"] = new JArray(new JObject
            {
                ["name"] = StageLoraName,
                ["weight"] = 1.0,
            });
        }

        JObject workflow = await fixture.GenerateAsync(MakeDocument(clip));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        UNETLoaderNode loader = AssertSingleCoreBaseLoader(bridge);
        LoraLoaderNode lora = Assert.Single(bridge.Graph.NodesOfType<LoraLoaderNode>());
        Assert.All(
            [StageSampler(bridge, 0), StageSampler(bridge, 1)],
            sampler =>
            {
                Assert.True(ReachesUpstream(bridge, sampler, loader.Id));
                Assert.True(ReachesUpstream(bridge, sampler, lora.Id));
            });

        live.AssertAllLive(loader, lora);
        AssertShippable(bridge, workflow, live);
    }

    // Dedup adopts loaders and conditioning; reserved ids let stages replace samplers and decodes.
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

        // LTX uses tiled decode; H3 inserts an A/V split before decode.
        ComfyNode decode = Assert.IsAssignableFrom<ComfyNode>(
            Assert.Single(bridge.Graph.Nodes.Values.OfType<IVaeDecode>()));
        Assert.Equal(CoreDecodeId, decode.Id);
        Assert.True(ReachesUpstream(bridge, decode, sampler.Id));
        Assert.Same(decode, live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive(sampler, decode);
        AssertShippable(bridge, workflow, live);
    }

    // Core reserves 6 for positive conditioning and 7 for negative conditioning.
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

    // Typed latent creation bypasses dedup, so LTX claims the id before the joint latent dedups.
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

    // A stale dedup entry would let the second family reuse the first family's claimed latent.
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

    // Only one stage can claim the host root.
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

    // The base image remains an input to image-to-video stages.
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

    // A later clip must not pin the discarded host root or read another clip's claimed output.
    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public async Task A_later_MiniMax_host_reference_cannot_block_the_text_root_claim(string source)
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
        Assert.Equal(CoreSamplerId, wanStage.Id);
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmKSamplerNode>().Count());
        Assert.Empty(bridge.Graph.NodesOfType<SwarmMiniMaxH3AddKeyframesNode>());

        live.AssertAllLive(wanStage, StageSampler(bridge, 1));
        AssertShippable(bridge, workflow, live);
    }

    // Root ownership belongs to the timeline, regardless of which architecture generates first.
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

    // Text-to-video discards host references before generation, so they cannot prevent adoption.
    [Fact]
    public async Task An_ltx_base_frame_ref_costs_the_timeline_nothing_on_text_to_video()
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

    // Text-to-video normalizes ImageReference to Generated; it should not warn about host captures.
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

    // Interpolation consumes the assembled timeline after the intermediate save.
    [Fact]
    public async Task Deferred_interpolation_keeps_the_root_claim_and_one_intermediate()
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

        Assert.Equal(CoreSamplerId, StageSampler(bridge, 0).Id);
        Assert.Contains(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>(),
            save => save.Images.Connection?.Node?.Id == CoreDecodeId);

        AssertShippable(bridge, workflow, live, publishedVideoSaves: 2);
    }

    // Record removed ids and their original classes to distinguish adoption from replacement.
    private static IReadOnlyDictionary<string, string> SweptHostRootNodes(
        WorkflowGenerator generator) =>
        VideoGraphHelpers.ReadRemovalRecord(generator, Constants.SweptHostRootNodesKey);

    // A fully adopted root leaves nothing for extension cleanup to remove.
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

    // LTX's dynamically allocated conditioning, A/V split, and audio decode are core-collectable.
    // Mixed families can leave nodes outside core's AutoCleanupNodeTypes.
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

    // Matching prompts share encoders; different seeds still require separate samplers.
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
