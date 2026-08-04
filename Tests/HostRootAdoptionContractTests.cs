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
}
