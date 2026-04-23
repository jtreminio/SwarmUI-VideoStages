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
    public void Native_ltx_stage_ignores_legacy_refiner_preprocess_when_no_clip_refs_are_defined()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Refiner", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false).Concat([SeedExistingRefinerPreprocessStep()]));
        StageRefStore store = new(generator);

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, preprocessNodes.Count);
        WorkflowNode imgToVideoNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        WorkflowNode preprocessNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image")[0]}");
        Assert.Equal("LTXVPreprocess", $"{preprocessNode.Node["class_type"]}");
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            store.Generated);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(WorkflowAssertions.RequireNodeById(workflow, "210").Node, "image"),
            new JArray("209", 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));
    }

    [Fact]
    public void Native_ltx_stage_ignores_legacy_base_preprocess_when_no_clip_refs_are_defined()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithLatentBaseCaptureAndExistingPreprocess(attachAudioToCurrentMedia: false));
        StageRefStore store = new(generator);

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, preprocessNodes.Count);
        WorkflowNode imgToVideoNode = Assert.Single(
            WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));
        WorkflowNode preprocessNode = WorkflowAssertions.RequireNodeById(
            workflow,
            $"{WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image")[0]}");
        Assert.Equal("LTXVPreprocess", $"{preprocessNode.Node["class_type"]}");
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNode.Node, "image"),
            store.Generated);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNode.Node, "image"),
            new JArray(preprocessNode.Id, 0)));

        List<WorkflowNode> decodeNodes = WorkflowUtils.NodesOfType(workflow, "VAEDecodeTiled")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Contains("8", decodeNodes.Select(node => node.Id));
    }

    [Fact]
    public void Native_ltx_stage_uses_current_source_instead_of_downstream_reference_preprocess_when_no_clip_refs_are_defined()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", control: 0.5, steps: 10)
        ).ToString();

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
