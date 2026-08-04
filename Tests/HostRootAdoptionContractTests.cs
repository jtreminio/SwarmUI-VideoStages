using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
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

        VAEDecodeNode decode = Assert.Single(bridge.Graph.NodesOfType<VAEDecodeNode>());
        Assert.Equal(CoreDecodeId, decode.Id);
        Assert.Same(sampler, decode.Samples.Connection?.Node);
        Assert.Same(decode, live.FinalVideoSave().Images.Connection?.Node);

        live.AssertAllLive(sampler, decode);
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
}
