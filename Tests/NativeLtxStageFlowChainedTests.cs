using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Chained_native_ltx_stage_with_prior_latent_upscale_keeps_previous_stage_as_second_guide_reference()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(
                models.VideoModel.Name,
                "Base",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: LtxV23SpatialUpscaler,
                steps: 10),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 1.0,
                upscaleMethod: LtxV23SpatialUpscaler,
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

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: LtxV23SpatialUpscaler,
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
    public void Chained_native_ltx_latent_upscale_uses_latent_upscale_by_node_without_vae_round_trip()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: "latent-bislerp",
                steps: 8));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LatentUpscaleByNode upscale = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", upscale.UpscaleMethod.LiteralAsString());
        Assert.Equal(2.0, upscale.ScaleBy.LiteralAsDouble());

        // The latent method upscales in latent space, so it must not introduce the model-upscaler scaffolding.
        Assert.Empty(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());

        // The prior stage's AV latent must be split by a single LTXVSeparateAVLatent feeding both the
        // upscaled video branch and the reused audio latent — never two separates each using one output.
        AssertNoAvLatentSplitTwice(bridge);
    }

    // A LATENT_AUDIOVIDEO tensor (e.g. a sampler output) split by more than one LTXVSeparateAVLatent is
    // the duplicate-separate bug: one node's video and another node's audio are used, each leaving its
    // other output dangling. Reusing a single separate (or reading a concat's pre-join tensors) avoids it.
    private static void AssertNoAvLatentSplitTwice(WorkflowBridge bridge)
    {
        IEnumerable<IGrouping<string, LTXVSeparateAVLatentNode>> grouped = bridge.Graph
            .NodesOfType<LTXVSeparateAVLatentNode>()
            .Where(separate => separate.AvLatent.Connection is not null)
            .GroupBy(separate => $"{separate.AvLatent.Connection.Node.Id}:{separate.AvLatent.Connection.SlotIndex}");

        foreach (IGrouping<string, LTXVSeparateAVLatentNode> group in grouped)
        {
            string ids = string.Join(", ", group.Select(separate => separate.Id));
            Assert.True(
                group.Count() == 1,
                $"AV latent '{group.Key}' is split by {group.Count()} LTXVSeparateAVLatent nodes ({ids}); expected exactly one.");
        }
    }

    [Fact]
    public void Chained_native_ltx_generated_reference_tracks_previous_stage_output_and_skips_redundant_guide_reinjection()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                upscale: 2.0,
                upscaleMethod: LtxV23SpatialUpscaler,
                steps: 10),
            MakeStage(
                models.VideoModel.Name,
                "Generated",
                control: 0.5,
                upscale: 1.0,
                upscaleMethod: LtxV23SpatialUpscaler,
                steps: 12));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());

        List<SwarmKSamplerNode> samplerNodes = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplerNodes.Count);
        Assert.True(ReachesUpstream(bridge, samplerNodes[1].LatentImage.Connection!.Node, samplerNodes[0].Id));

        WorkflowNode finalVideoDecode = AsWorkflowNode(RequireTypedNode<VAEDecodeNode>(bridge, "202"), workflow);
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

        string stagesJson = JsonSingleClipStages(
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
        AssertSamplerUsesConditioningNode(samplers[0], conditioningNodes[0].Id);
        AssertSamplerUsesConditioningNode(samplers[1], conditioningNodes[1].Id);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal("9", saveNode.Id);
        INodeOutput finalDecodeOutput = bridge.Graph.GetNode<VAEDecodeNode>("202")!.IMAGE;
        Assert.Same(finalDecodeOutput, saveNode.Images.Connection);

        IReadOnlyList<LTXVSeparateAVLatentNode> separateNodes = bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>();
        Assert.True(separateNodes.Count >= 3);
        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsUseProgressiveAudio(workflow, originalSeparate);

        VAEDecodeNode finalVideoDecode = RequireTypedNode<VAEDecodeNode>(bridge, "202");
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
            MakeClipWithRefs(refs: [MakeRef("Base", frame: 2)], stage)
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
    public void Guide_ref_miss_throws_user_error_during_workflow_run()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = MakeRootConfig(
            width: 512,
            height: 512,
            MakeClip(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10),
                MakeStage(models.VideoModel.Name, "edit99", control: 0.5, steps: 10)),
            MakeClip(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10))
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true)));
        Assert.Contains("Clip 0 stage 1", ex.Message);
        Assert.Contains("could not resolve ImageReference 'edit99'", ex.Message);
    }
}
