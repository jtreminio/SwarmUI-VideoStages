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

public partial class StageFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Displaced_root_only_retargets_or_suppresses_its_owned_save(bool doNotSave)
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeRootConfig(512, 512, MakeGeneratedClip(models)).ToString());
        input.Set(T2IParamTypes.DoNotSave, doNotSave);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                new[]
                {
                    SeedRawTextToVideoAvLatentRootStep(),
                    WorkflowTestHarness.CorePreVideoSavePrepStep(),
                    SeedUnrelatedPublicationAndSharedRootLoaderSinkStep()
                }
                .Concat(WorkflowTestHarness.VideoStagesSteps()),
                features: InitVideoClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmSaveAnimationWSNode unrelatedSave =
            Assert.IsType<SwarmSaveAnimationWSNode>(bridge.Graph.GetNode("801"));
        Assert.Equal("800", unrelatedSave.Images.Connection?.Node.Id);
        Assert.NotNull(bridge.Graph.GetNode("101"));
        Assert.NotNull(bridge.Graph.GetNode("802"));
        Assert.False(workflow.ContainsKey("10"), "The displaced root sampler survived cleanup.");

        List<SwarmSaveAnimationWSNode> saves = [
            .. bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>()
        ];
        if (doNotSave)
        {
            Assert.Single(saves);
            Assert.Same(unrelatedSave, saves[0]);
        }
        else
        {
            Assert.Equal(2, saves.Count);
            Assert.Single(
                saves,
                save => save.Id != unrelatedSave.Id
                    && JToken.DeepEquals(
                        WorkflowBridge.ToPath(save.Images.Connection!),
                        generator.CurrentMedia.Path));
        }
    }

    /// <summary>
    /// The manufactured half of the "Node 101 not found" regression, kept on the stub harness
    /// because a POST cannot build this shape: the root VAE must be a node the model-gen step
    /// deduplicated onto (<c>LoadingVAE = ["101", 0]</c>) while the root is still a raw, undecoded
    /// AV latent. Core's <c>automaticvae</c> path shares a loader too, but by producing one loader —
    /// not by aliasing a seeded id — so it cannot fail the way the shipped bug did.
    /// <para>
    /// <see cref="ArchitectureContractGraphTests.Multi_clip_merge_keeps_the_shared_root_vae_loader_the_clip_decodes_read"/>
    /// is the end-to-end half.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("cut")]
    [InlineData("crossfade")]
    public void Multi_clip_merge_keeps_the_seeded_root_vae_loader_the_clip_decodes_read(
        string boundaryOut)
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        // Simulate architecture reload deduplication onto the root VAE without loading a model.
        WorkflowGenerator.AddModelGenStep(
            g =>
            {
                g.LoadingModel = new JArray("4", 0);
                g.LoadingClip = new JArray("4", 1);
                g.LoadingVAE = new JArray("101", 0);
            },
            -1000);
        JObject first = MakeGeneratedClip(models);
        first["boundaryOut"] = boundaryOut;
        first["boundaryOutOverlap"] = 8;
        T2IParamInput input = BuildTextToVideoInput(
            models.VideoModel,
            MakeRootConfig(512, 512, first, MakeGeneratedClip(models)).ToString());

        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                new[]
                {
                    SeedRawTextToVideoAvLatentRootStep(),
                    WorkflowTestHarness.CorePreVideoSavePrepStep()
                }
                .Concat(WorkflowTestHarness.VideoStagesSteps()),
                features: InitVideoClipFeatures);

        // The displaced-root sweep must preserve the VAE shared by both clip decodes.
        AssertNoDanglingNodeRefs(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode rootVae = bridge.Graph.GetNode("101");
        Assert.NotNull(rootVae);
        List<VAEDecodeTiledNode> decodes =
            [.. bridge.Graph.NodesOfType<VAEDecodeTiledNode>()];
        Assert.Equal(2, decodes.Count);
        Assert.All(decodes, decode => Assert.Equal("101", decode.Vae.Connection?.Node.Id));
    }

    private static WorkflowGenerator.WorkflowGenStep SeedUnrelatedPublicationAndSharedRootLoaderSinkStep() =>
        new(g =>
        {
            using WorkflowBridge bridge = BridgeSync.For(g);
            UnknownNode unrelatedMedia = bridge.AddStub(
                "UnitTest_UnrelatedVideo",
                "800").WithOutputs(WGNodeData.DT_VIDEO);
            var unrelatedSave = new SwarmSaveAnimationWSNode().With(
                Fps: 12.0,
                Lossless: false,
                Quality: 80,
                Method: "default",
                Format: "h264-mp4");
            unrelatedSave.Images.ConnectToUntyped(unrelatedMedia.GetOutput(0));
            bridge.AddNode(unrelatedSave, "801");

            ComfyNode sharedRootVae = bridge.Graph.GetNode("101")
                ?? throw new InvalidOperationException("Expected seeded root VAE loader.");
            UnknownNode unrelatedSink = bridge.AddStub("UnitTest_UnrelatedPreview", "802");
            unrelatedSink.GetInput("images").ConnectToUntyped(sharedRootVae.FindOutput(0));
        }, 11.25);
}
