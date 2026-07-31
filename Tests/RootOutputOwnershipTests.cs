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

    [Theory]
    [InlineData("cut")]
    [InlineData("crossfade")]
    public void Multi_clip_merge_keeps_the_shared_root_vae_loader_the_clip_decodes_read(
        string boundaryOut)
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        // A real diffusion_models-format LTX-2 stage model resets LoadingVAE, and the arch reload
        // that follows dedups straight back onto the root's own VAE loader — so every stage decode
        // reads node 101. This hook pins that exact sharing without the model-download machinery.
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

        (JObject workflow, WorkflowGenerator _generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                new[]
                {
                    SeedRawTextToVideoAvLatentRootStep(),
                    WorkflowTestHarness.CorePreVideoSavePrepStep()
                }
                .Concat(WorkflowTestHarness.VideoStagesSteps()),
                features: InitVideoClipFeatures);

        // The merged video reaches the save through a BatchImagesNode autogrow list, and the root
        // VAE loader is reachable only through it. Every clip decode still reads that loader, so the
        // displaced-root sweep must see it as live — dropping it leaves the decodes pointing at a
        // node Comfy no longer has ("Node 101 not found" at execution time).
        AssertNoDanglingNodeRefs(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode rootVae = bridge.Graph.GetNode("101");
        Assert.NotNull(rootVae);
        List<VAEDecodeNode> decodes = [.. bridge.Graph.NodesOfType<VAEDecodeNode>()];
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
