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
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithLatentBaseCaptureAndDownstreamRefinerPreprocess(attachAudioToCurrentMedia: false));
        StageRefStore store = new(generator);

        WorkflowNode imgToVideoNode = WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace")
            .Single(node => node.Id != "111");
        WorkflowNode stagePreprocess = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image")[0]}");
        Assert.Equal("LTXVPreprocess", $"{stagePreprocess.Node["class_type"]}");
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(stagePreprocess.Node, "image"),
            store.Generated);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(stagePreprocess.Id, 0)));
    }
}
