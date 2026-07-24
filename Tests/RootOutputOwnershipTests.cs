using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;

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
                features: SourcedClipFeatures);
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
