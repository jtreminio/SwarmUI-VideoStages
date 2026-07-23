using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Characterization coverage for the plan-backed adapter path. These cases run through the
/// canonical LTX plan and assert the same graph-level outcomes as the established legacy flows.
/// </summary>
public partial class StageFlowTests
{
    [Fact]
    public void Plan_adapter_single_stage_ltx_preserves_one_stage_graph()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8)));

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
    }

    [Fact]
    public void Plan_adapter_multi_stage_ltx_preserves_stage_chaining()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.True(ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, samplers[0].Id));
    }

    [Fact]
    public void Plan_adapter_sourced_ltx_preserves_source_refine_graph()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        (JObject workflow, WorkflowGenerator unusedGenerator) = GenerateSourcedFlow(models, MakeSourcedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Single(SamplerNodesOrdered(bridge));
    }

    [Fact]
    public void Plan_adapter_multi_clip_ltx_preserves_parallel_merge_graph()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        (JObject workflow, WorkflowGenerator unusedGenerator) = GenerateSourcedFlow(
            models,
            MakeGeneratedClip(models),
            MakeGeneratedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, SamplerNodesOrdered(bridge).Count);
        Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
    }

    [Fact]
    public void Three_stage_intermediate_publications_remain_bound_to_distinct_stage_artifacts()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 9),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));
        input.Set(T2IParamTypes.OutputIntermediateImages, true);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<ComfyNode> saves = [
            .. bridge.Graph.Nodes.Values.Where(
                node => node is SwarmSaveAnimationWSNode or SwarmSaveImageWSNode)
        ];
        Assert.True(
            saves.Count == 3,
            $"Expected three publications, found {saves.Count}; intermediate flag="
            + $"{generator.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)}; "
            + $"stage count={generator.GetVideoStagesSpec().Clips.Sum(clip => clip.Stages.Count)}; "
            + $"save-like classes={string.Join(",", bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName.Contains("Save")).Select(node => node.ClassTypeName))}");
        string[] publishedPaths = [
            .. saves.Select(
                save => WorkflowBridge.ToPath(save.FindInput("images").Connection!).ToString())
        ];
        Assert.Equal(3, publishedPaths.Distinct().Count());
        Assert.Single(
            saves,
            save => JToken.DeepEquals(
                WorkflowBridge.ToPath(save.FindInput("images").Connection!),
                generator.CurrentMedia.Path));
    }

    [Fact]
    public void Do_not_save_suppresses_host_and_intermediate_publications()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 9),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.DoNotSave, true);

        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.DoesNotContain(
            bridge.Graph.Nodes.Values,
            node => node is SwarmSaveAnimationWSNode or SwarmSaveImageWSNode);
    }
}
