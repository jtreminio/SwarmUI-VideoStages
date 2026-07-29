using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Authored_guide_uses_the_clips_green_fit_framing()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Base", steps: 8));
        clip["refFraming"] = Constants.ReferenceFramingFitGreen;

        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            new JArray(clip).ToString());
        (JObject workflow, WorkflowGenerator _) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVImgToVideoInplaceNode guide =
            Assert.Single(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        LTXVPreprocessNode preprocess =
            Assert.IsType<LTXVPreprocessNode>(guide.Image.Connection?.Node);
        SwarmFrameImageNode frame =
            Assert.IsType<SwarmFrameImageNode>(preprocess.Image.Connection?.Node);
        Assert.Equal(
            Constants.ReferenceFramingFitGreen,
            frame.Method.LiteralAsString());
    }

    [Theory]
    [InlineData("Base")]
    [InlineData("Refiner")]
    public void Authored_root_reference_drives_the_selected_stage_guide(string referenceName)
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 8),
            MakeStage(models.VideoModel.Name, referenceName, steps: 8));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVImgToVideoInplaceNode selectedGuide = bridge.Graph
            .NodesOfType<LTXVImgToVideoInplaceNode>()
            .OrderBy(node => int.Parse(node.Id))
            .Last();
        StageRefStore store = new(generator);
        StageRefStore.StageRef expected = referenceName == "Base"
            ? store.Base
            : store.Refiner;

        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(selectedGuide.Image.Connection!),
            expected);
    }

    [Fact]
    public void Authored_stage_reference_drives_the_selected_stage_guide()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Base", steps: 8),
            MakeStage(models.VideoModel.Name, "Stage0", steps: 8));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        LTXVImgToVideoInplaceNode selectedGuide = bridge.Graph
            .NodesOfType<LTXVImgToVideoInplaceNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ElementAt(1);

        Assert.True(
            ReachesUpstream(bridge, selectedGuide.Image.Connection!.Node, samplers[0].Id),
            "Stage0's selected guide does not trace to this clip's first stage.");
    }

    [Fact]
    public void Stage_reference_numbers_are_local_to_each_clip()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        string stagesJson = MakeRootConfig(
            512,
            512,
            MakeClip(
                MakeStage(models.VideoModel.Name, "Base", steps: 8),
                MakeStage(models.VideoModel.Name, "Stage0", steps: 8)),
            MakeClip(
                MakeStage(models.VideoModel.Name, "Refiner", steps: 8),
                MakeStage(models.VideoModel.Name, "Stage0", steps: 8)))
            .ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        LTXVImgToVideoInplaceNode secondClipStageGuide = bridge.Graph
            .NodesOfType<LTXVImgToVideoInplaceNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ElementAt(3);

        Assert.True(
            ReachesUpstream(
                bridge,
                secondClipStageGuide.Image.Connection!.Node,
                samplers[2].Id),
            "Clip 1 Stage0 did not resolve to clip 1's first stage.");
        Assert.False(
            ReachesUpstream(
                bridge,
                secondClipStageGuide.Image.Connection!.Node,
                samplers[0].Id),
            "Clip 1 Stage0 incorrectly resolved to clip 0's first stage.");
    }

    [Fact]
    public void Sourced_stage_zero_explicit_generated_reference_uses_the_captured_root()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject sourced = MakeSourcedClip(models);
        ((JObject)((JArray)sourced["stages"])[0])["imageReference"] = "Generated";

        (JObject workflow, WorkflowGenerator generator) =
            GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourcedWindow = Assert.Single(
            bridge.Graph.NodesOfType<SwarmFrameWindowNode>());
        LTXVImgToVideoInplaceNode authoredGuide = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(authoredGuide.Image.Connection!),
            new StageRefStore(generator).Generated);
        Assert.False(
            ReachesUpstream(bridge, authoredGuide.Image.Connection!.Node, sourcedWindow.Id),
            "The explicit Generated guide was replaced by the sourced footage.");
    }
}
