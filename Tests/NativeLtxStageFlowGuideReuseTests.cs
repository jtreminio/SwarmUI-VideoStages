using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Native_ltx_stage_uses_the_authored_base_instead_of_a_downstream_generated_reference()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithLatentBaseCaptureAndDownstreamRefinerPreprocess(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        StageRefStore store = new(
            generator,
            Ltx2ArchitectureModule.ArchitectureId);

        LTXVImgToVideoInplaceNode imgToVideoNode = bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()
            .Single(node => node.Id != "111");
        LTXVPreprocessNode stagePreprocess = (LTXVPreprocessNode)imgToVideoNode.Image.Connection!.Node;
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(stagePreprocess.Image.Connection!),
            store.Base);
        Assert.Same(stagePreprocess.OutputImage, imgToVideoNode.Image.Connection);
    }
}
