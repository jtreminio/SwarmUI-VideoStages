using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Native_ltx_stage_reuses_existing_matching_preprocess_for_refiner_reference()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Refiner", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false).Concat([SeedExistingRefinerPreprocessStep()]));

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode preprocessNode = Assert.Single(preprocessNodes);
        Assert.Equal("210", preprocessNode.Id);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            new JArray("209", 0)));

        WorkflowNode addGuideNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(addGuideNode.Node, "image"),
            new JArray("210", 0)));
    }

    [Fact]
    public void Native_ltx_stage_reuses_existing_base_preprocess_when_base_reference_was_captured_as_latent()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithLatentBaseCaptureAndExistingPreprocess(attachAudioToCurrentMedia: false));

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        WorkflowNode preprocessNode = Assert.Single(preprocessNodes);
        Assert.Equal("210", preprocessNode.Id);

        WorkflowNode addGuideNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(addGuideNode.Node, "image"),
            new JArray("210", 0)));

        List<WorkflowNode> decodeNodes = WorkflowUtils.NodesOfType(workflow, "VAEDecodeTiled")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Contains("8", decodeNodes.Select(node => node.Id));
    }

    [Fact]
    public void Native_ltx_base_reference_does_not_reuse_preprocess_from_downstream_refiner_or_root_video_chain()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithLatentBaseCaptureAndDownstreamRefinerPreprocess(attachAudioToCurrentMedia: false));

        WorkflowNode stagePreprocess = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(stagePreprocess.Node, "image"),
            new JArray("24", 0)));

        WorkflowNode addGuideNode = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .Single(node => JToken.DeepEquals(
                WorkflowAssertions.RequireConnectionInput(node.Node, "image"),
                new JArray(stagePreprocess.Id, 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(addGuideNode.Node, "image"),
            new JArray(stagePreprocess.Id, 0)));
    }
}
