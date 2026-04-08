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
    public void Chained_native_ltx_stage_with_prior_latent_upscale_keeps_previous_stage_as_second_guide_reference()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(
                models.VideoModel.Name,
                "Base",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
                steps: 10),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 1.0,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
                steps: 12)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        StageRefStore store = new(generator);
        Assert.True(store.TryGetStageRef(0, out StageRefStore.StageRef stage0Ref));

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, preprocessNodes.Count);
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNodes[1].Node, "image"),
            stage0Ref);

        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(addGuideNodes[1].Node, "image"),
            new JArray(preprocessNodes[1].Id, 0)));
        AssertNoDanglingCropGuides(workflow);
    }

    [Fact]
    public void Chained_native_ltx_generated_reference_tracks_previous_stage_output_and_skips_redundant_guide_reinjection()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
                steps: 10),
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                upscale: 1.0,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
                steps: 12)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide"));

        List<WorkflowNode> samplerNodes = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, samplerNodes.Count);
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(samplerNodes[1].Node, "latent_image"),
            new JArray(samplerNodes[0].Id, 0)));

        IReadOnlyList<WorkflowNode> tiledDecodeNodes = WorkflowUtils.NodesOfType(workflow, "VAEDecodeTiled");
        WorkflowNode finalVideoDecode = Assert.Single(tiledDecodeNodes);
        Assert.Equal("202", finalVideoDecode.Id);
        AssertLtxFinalTiledDecodeUsesUpdatedDefaults(finalVideoDecode);
        AssertNoDanglingCropGuides(workflow);
        RequireRetargetedSeparateNode(workflow, finalVideoDecode);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }

    [Fact]
    public void Chained_native_ltx_latent_model_upscales_reuse_single_final_crop_node()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
                steps: 10),
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors",
                steps: 12)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        Assert.Equal(3, WorkflowUtils.NodesOfType(workflow, "LTXVCropGuides").Count);
        AssertNoDanglingCropGuides(workflow);
    }

    [Theory]
    [InlineData("PreviousStage")]
    [InlineData("Stage0")]
    public void Chained_native_ltx_stages_keep_audio_connected_and_stage_reference_only_changes_guide_image(string secondStageReference)
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = new JArray(
            MakeStage(models.VideoModel.Name, "Base", steps: 10),
            MakeStage(models.VideoModel.Name, secondStageReference, steps: 12)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        List<WorkflowNode> samplers = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, samplers.Count);
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "ImageFromBatch"));

        StageRefStore store = new(generator);
        Assert.True(store.TryGetStageRef(0, out StageRefStore.StageRef stage0Ref));
        Assert.NotNull(stage0Ref.Media);
        Assert.False(JToken.DeepEquals(stage0Ref.Media.Path, new JArray("202", 0)));

        List<WorkflowNode> preprocessNodes = WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, preprocessNodes.Count);
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowAssertions.RequireConnectionInput(preprocessNodes[1].Node, "image"),
            stage0Ref);

        List<WorkflowNode> addGuideNodes = WorkflowUtils.NodesOfType(workflow, "LTXVAddGuide")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, addGuideNodes.Count);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(addGuideNodes[1].Node, "image"),
            new JArray(preprocessNodes[1].Id, 0)));
        AssertSamplerConsumesGuideOutput(workflow, addGuideNodes[1], samplers[1]);
        AssertLtxConditioningUsesTextEncoders(workflow);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(samplers[0].Node, "positive"),
            new JArray(addGuideNodes[0].Id, 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(samplers[0].Node, "negative"),
            new JArray(addGuideNodes[0].Id, 1)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(samplers[1].Node, "positive"),
            new JArray(addGuideNodes[1].Id, 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(samplers[1].Node, "negative"),
            new JArray(addGuideNodes[1].Id, 1)));

        IReadOnlyList<WorkflowNode> saveNodes = WorkflowUtils.NodesOfType(workflow, "SwarmSaveAnimationWS");
        WorkflowNode saveNode = Assert.Single(saveNodes);
        Assert.Equal("9", saveNode.Id);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(saveNode.Node, "images"),
            new JArray("202", 0)));

        IReadOnlyList<WorkflowNode> separateNodes = WorkflowUtils.NodesOfType(workflow, "LTXVSeparateAVLatent");
        Assert.True(separateNodes.Count >= 3);
        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsReuseOriginalAudio(workflow, originalSeparate);

        WorkflowNode finalVideoDecode = WorkflowAssertions.RequireNodeById(workflow, "202");
        Assert.Equal("VAEDecodeTiled", $"{finalVideoDecode.Node["class_type"]}");
        AssertLtxFinalTiledDecodeUsesUpdatedDefaults(finalVideoDecode);
        WorkflowNode finalSeparate = RequireRetargetedSeparateNode(workflow, finalVideoDecode);

        WorkflowNode finalAudioDecode = WorkflowAssertions.RequireNodeById(workflow, "203");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(finalAudioDecode.Node, "samples"),
            new JArray(finalSeparate.Id, 1)));
        AssertNoDanglingTiledVaeDecodes(workflow);
        AssertNoDanglingCropGuides(workflow);
        AssertWorkflowHasNoCycles(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }
}
