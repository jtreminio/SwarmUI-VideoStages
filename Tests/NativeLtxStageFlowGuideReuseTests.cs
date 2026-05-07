using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Native_ltx_stage_uses_current_source_instead_of_downstream_reference_preprocess_when_no_clip_refs_are_defined()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithLatentBaseCaptureAndDownstreamRefinerPreprocess(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        StageRefStore store = new(generator);

        LTXVImgToVideoInplaceNode imgToVideoNode = bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()
            .Single(node => node.Id != "111");
        LTXVPreprocessNode stagePreprocess = (LTXVPreprocessNode)imgToVideoNode.Image.Connection!.Node;
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(stagePreprocess.Image.Connection!),
            store.Generated);
        Assert.Same(stagePreprocess.OutputImage, imgToVideoNode.Image.Connection);
    }
}
