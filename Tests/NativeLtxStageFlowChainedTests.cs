using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
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

        string stagesJson = JsonSingleClipStages512(
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
                steps: 12));

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

        List<WorkflowNode> imgToVideoNodes = WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, imgToVideoNodes.Count);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNodes[1].Node, "image"),
            new JArray(preprocessNodes[1].Id, 0)));
    }

    [Fact]
    public void Chained_native_ltx_pixel_upscale_after_latent_upscale_is_ignored_without_image_scale_scaffolding()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
                steps: 8),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "pixel-lanczos",
                steps: 8));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        List<WorkflowNode> samplerNodes = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(3, samplerNodes.Count);

        Assert.Single(WorkflowUtils.NodesOfType(workflow, "LTXVLatentUpsampler"));

        Assert.DoesNotContain(
            WorkflowUtils.NodesOfType(workflow, "ImageScale"),
            node => node.Node["inputs"]?.Value<int>("width") == 2048
                && node.Node["inputs"]?.Value<int>("height") == 2048
                && $"{node.Node["inputs"]?["crop"]}" == "disabled");
    }

    [Fact]
    public void Chained_native_ltx_generated_reference_tracks_previous_stage_output_and_skips_redundant_guide_reinjection()
    {
        using SwarmUiTestContext _ = new();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
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
                steps: 12));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVPreprocess"));
        Assert.Empty(WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace"));

        List<WorkflowNode> samplerNodes = WorkflowAssertions.NodesOfAnyType(workflow, "KSamplerAdvanced", "SwarmKSampler")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, samplerNodes.Count);
        Assert.True(OutputTracesBackToSource(
            workflow,
            WorkflowAssertions.RequireConnectionInput(samplerNodes[1].Node, "latent_image"),
            new JArray(samplerNodes[0].Id, 0)));

        WorkflowNode finalVideoDecode = WorkflowAssertions.RequireNodeById(workflow, "202");
        AssertLtxFinalDecodeUsesPlainVaeDecode(finalVideoDecode);
        RequireRetargetedSeparateNode(workflow, finalVideoDecode);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
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

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Base", steps: 10),
            MakeStage(models.VideoModel.Name, secondStageReference, steps: 12));

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

        List<WorkflowNode> imgToVideoNodes = WorkflowUtils.NodesOfType(workflow, "LTXVImgToVideoInplace")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, imgToVideoNodes.Count);
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(imgToVideoNodes[1].Node, "image"),
            new JArray(preprocessNodes[1].Id, 0)));
        AssertSamplerConsumesImgToVideoOutput(workflow, imgToVideoNodes[1], samplers[1]);
        IReadOnlyList<WorkflowNode> conditioningNodes = AssertLtxConditioningUsesAdvancedEncoders(workflow);
        Assert.Equal(2, conditioningNodes.Count);
        AssertSamplerUsesConditioningNode(samplers[0], conditioningNodes[0]);
        AssertSamplerUsesConditioningNode(samplers[1], conditioningNodes[1]);

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
        AssertLtxFinalDecodeUsesPlainVaeDecode(finalVideoDecode);
        WorkflowNode finalSeparate = RequireRetargetedSeparateNode(workflow, finalVideoDecode);

        WorkflowNode finalAudioDecode = WorkflowAssertions.RequireNodeById(workflow, "203");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(finalAudioDecode.Node, "samples"),
            new JArray(finalSeparate.Id, 1)));
        AssertNoDanglingTiledVaeDecodes(workflow);
        AssertWorkflowHasNoCycles(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }
}
