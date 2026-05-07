using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Chained_native_ltx_stage_with_prior_latent_upscale_keeps_previous_stage_as_second_guide_reference()
    {
        using SwarmUiTestContext _ = new();
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
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<LTXVPreprocessNode> preprocessNodes = [.. bridge.Graph.NodesOfType<LTXVPreprocessNode>().OrderBy(node => int.Parse(node.Id))];
        Assert.Equal(2, preprocessNodes.Count);

        List<LTXVImgToVideoInplaceNode> imgToVideoNodes = bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, imgToVideoNodes.Count);
        Assert.Same(preprocessNodes[1].OutputImage, imgToVideoNodes[1].Image.Connection);
    }

    [Fact]
    public void Chained_native_ltx_pixel_upscale_after_latent_upscale_is_ignored_without_image_scale_scaffolding()
    {
        using SwarmUiTestContext _ = new();
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplerNodes = SamplerNodesOrdered(bridge);
        Assert.Equal(3, samplerNodes.Count);

        Assert.Single(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());

        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<ImageScaleNode>(),
            node => node.Width.LiteralAsInt() == 2048
                && node.Height.LiteralAsInt() == 2048
                && node.Crop.LiteralAsString() == "disabled");
    }

    [Fact]
    public void Chained_native_ltx_generated_reference_tracks_previous_stage_output_and_skips_redundant_guide_reinjection()
    {
        using SwarmUiTestContext _ = new();
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
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        List<SwarmKSamplerNode> samplerNodes = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplerNodes.Count);
        Assert.True(OutputTracesBackToSource(
            workflow,
            SamplerLatentImagePath(samplerNodes[1]),
            new JArray(samplerNodes[0].Id, 0)));

        WorkflowNode finalVideoDecode = AsWorkflowNode(RequireTypedNode<VAEDecodeNode>(bridge, "202"), workflow);
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
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages512(
            MakeStage(models.VideoModel.Name, "Base", steps: 10),
            MakeStage(models.VideoModel.Name, secondStageReference, steps: 12));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.Empty(bridge.Graph.NodesOfType<ImageFromBatchNode>());

        List<LTXVPreprocessNode> preprocessNodes = bridge.Graph.NodesOfType<LTXVPreprocessNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, preprocessNodes.Count);

        List<LTXVImgToVideoInplaceNode> imgToVideoNodes = bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.Equal(2, imgToVideoNodes.Count);
        Assert.Same(preprocessNodes[1].OutputImage, imgToVideoNodes[1].Image.Connection);
        AssertSamplerConsumesImgToVideoOutput(workflow, AsWorkflowNode(imgToVideoNodes[1], workflow), AsWorkflowNode(samplers[1], workflow));
        IReadOnlyList<WorkflowNode> conditioningNodes = AssertLtxConditioningUsesAdvancedEncoders(workflow);
        Assert.Equal(2, conditioningNodes.Count);
        AssertSamplerUsesConditioningNode(AsWorkflowNode(samplers[0], workflow), conditioningNodes[0]);
        AssertSamplerUsesConditioningNode(AsWorkflowNode(samplers[1], workflow), conditioningNodes[1]);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal("9", saveNode.Id);
        INodeOutput finalDecodeOutput = bridge.Graph.GetNode<VAEDecodeNode>("202")!.IMAGE;
        Assert.Same(finalDecodeOutput, saveNode.Images.Connection);

        IReadOnlyList<LTXVSeparateAVLatentNode> separateNodes = bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>();
        Assert.True(separateNodes.Count >= 3);
        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsUseProgressiveAudio(workflow, originalSeparate);

        VAEDecodeNode finalVideoDecode = RequireTypedNode<VAEDecodeNode>(bridge, "202");
        AssertLtxFinalDecodeUsesPlainVaeDecode(AsWorkflowNode(finalVideoDecode, workflow));
        WorkflowNode finalSeparate = RequireRetargetedSeparateNode(workflow, AsWorkflowNode(finalVideoDecode, workflow));

        LTXVAudioVAEDecodeNode finalAudioDecode = RequireTypedNode<LTXVAudioVAEDecodeNode>(bridge, "203");
        LTXVSeparateAVLatentNode finalSeparateTyped = RequireTypedNode<LTXVSeparateAVLatentNode>(bridge, finalSeparate.Id);
        Assert.Same(finalSeparateTyped.AudioLatent, finalAudioDecode.Samples.Connection);
        AssertNoDanglingTiledVaeDecodes(workflow);
        AssertWorkflowHasNoCycles(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }

    [Fact]
    public void Ltx_add_guide_positive_and_negative_inputs_trace_through_ltxv_conditioning_node()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", steps: 10);
        stage["refStrengths"] = new JArray(0.55);
        string stagesJson = new JArray(
            MakeClipWithRefs(width: 512, height: 512, refs: [MakeRef("Base", frame: 2)], stage)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WorkflowNode conditioningWorkflowNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        LTXVConditioningNode conditioningNode = RequireTypedNode<LTXVConditioningNode>(bridge, conditioningWorkflowNode.Id);
        LTXVAddGuideNode addGuideNode = Assert.Single(bridge.Graph.NodesOfType<LTXVAddGuideNode>());

        Assert.Same(conditioningNode.Positive, addGuideNode.PositiveInput.Connection);
        Assert.Same(conditioningNode.Negative, addGuideNode.NegativeInput.Connection);
    }

    [Fact]
    public void Chained_native_ltx_reuse_audio_uses_first_stage_audio_for_later_stages()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject clip = MakeClip(
            width: 512,
            height: 512,
            MakeStage(models.VideoModel.Name, "Base", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 12),
            MakeStage(models.VideoModel.Name, "PreviousStage", steps: 14));
        clip["reuseAudio"] = true;
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));

        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsReuseFirstStageAudio(workflow, originalSeparate);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Skip_rest_of_clip_after_guide_ref_miss_does_not_cascade_to_next_clip()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
            width: 512,
            height: 512,
            MakeClip(
                width: 512,
                height: 512,
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10),
                MakeStage(models.VideoModel.Name, "edit99", control: 0.5, steps: 10)),
            MakeClip(
                width: 512,
                height: 512,
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, SamplerNodesOrdered(bridge).Count);
        Assert.Equal(2, bridge.Graph.NodesOfType<LTXVConditioningNode>().Count);
    }

    private static JArray SamplerLatentImagePath(ComfyNode samplerNode)
    {
        INodeInput latentInput = samplerNode.FindInput("latent_image");
        Assert.NotNull(latentInput);
        Assert.NotNull(latentInput.Connection);
        return new JArray(latentInput.Connection.Node.Id, latentInput.Connection.SlotIndex);
    }
}
