using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.HostVideo;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    /// <summary>
    /// SVD has no architecture module, so a timeline naming it must land on the host-video
    /// fallback — and that fallback has to drive core's real SVD graph, not merely claim the clip.
    /// </summary>
    [Fact]
    public async Task Active_SVD_configuration_uses_the_host_video_fallback()
    {
        using SvdWorkflowFixture fixture = SvdWorkflowFixture.CreateWithBaseModel();

        (JObject workflow, WorkflowGenerator generator) =
            await ComfyWorkflowApiTestHarness.GenerateWithStateAsync(
                fixture.ImageToVideoPost(MakeDocument(MakeClip(1.0, fixture.Stage()))));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        WorkflowLivePath live = WorkflowLivePath.For(bridge);

        Assert.Equal(
            HostVideoArchitectureModule.ArchitectureId,
            Assert.Single(generator.RequireVideoExecutionPlanContext().Plan.Clips)
                .Architecture.Id);

        ImageOnlyCheckpointLoaderNode loader = Assert.Single(
            bridge.Graph.NodesOfType<ImageOnlyCheckpointLoaderNode>());
        SVDImg2vidConditioningNode conditioning = Assert.Single(
            bridge.Graph.NodesOfType<SVDImg2vidConditioningNode>());
        Assert.Same(loader.CLIPVISION, conditioning.ClipVision.Connection);
        Assert.Equal(
            VideoStagesWorkflowFixture.RequestedFrames,
            conditioning.VideoFrames.LiteralAsInt());

        SwarmKSamplerNode stage = StageSampler(bridge, 0);
        Assert.Same(conditioning.Positive, stage.Positive.Connection);
        Assert.Same(conditioning.Latent, stage.LatentImage.Connection);

        live.AssertAllLive(loader, conditioning, stage);

        // KNOWN CORE DEFECT: ImageToVideoGenInfo.PrepModelAndCond always calls CreateModelLoader
        // for the video model, and the SVD branch of PrepFullCond then replaces the model, VAE and
        // CLIP-vision with its own ImageOnlyCheckpointLoader — so the first loader ships with no
        // consumer. Not the extension's: nothing in VideoStages loads the stage checkpoint itself,
        // and the other host-video families keep the loader core hands them. Comfy only executes an
        // output's ancestors, so this wastes a node in the prompt, not VRAM. Pinned rather than
        // dropping AssertNoOrphanNodes, which would drop coverage of every other orphan too.
        CheckpointLoaderSimpleNode stranded = Assert.Single(
            bridge.Graph.NodesOfType<CheckpointLoaderSimpleNode>(),
            node => node.CkptName.LiteralAsString() == fixture.Model.Name);
        Assert.Equal(
            [$"{CheckpointLoaderSimpleNode.ClassType}#{stranded.Id}"],
            live.OrphanNodes());
        AssertNoDanglingNodeRefs(workflow);
        AssertAcyclic(bridge);
    }

    [Fact]
    public void Active_mixed_model_configuration_fails_before_execution()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated"),
                MakeStage("not-an-ltx-model", "PreviousStage")));

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true)));

        Assert.Contains("does not resolve to a registered video architecture", error.Message);
    }
}
