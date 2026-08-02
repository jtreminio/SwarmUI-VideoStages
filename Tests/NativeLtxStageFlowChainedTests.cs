using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
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

        Assert.Empty(bridge.Graph.NodesOfType<LTXVLatentUpsamplerNode>());

        AssertNoAvLatentSplitTwice(bridge);
    }

    [Fact]
    public void Chained_native_ltx_latent_downscale_applies_the_latent_scale_node_too()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 0.5,
                upscaleMethod: "latent-bislerp",
                steps: 8));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LatentUpscaleByNode downscale = Assert.Single(bridge.Graph.NodesOfType<LatentUpscaleByNode>());
        Assert.Equal("bislerp", downscale.UpscaleMethod.LiteralAsString());
        Assert.Equal(0.5, downscale.ScaleBy.LiteralAsDouble());
        AssertNoAvLatentSplitTwice(bridge);
    }

    // Each AV latent must be split once so its video and audio outputs share the same separator.
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

        Assert.Single(
            bridge.Graph.Nodes.Values,
            node => (node is VAEDecodeNode or VAEDecodeTiledNode)
                && ReachesUpstream(bridge, node, samplerNodes[1].Id));
        Assert.DoesNotContain(
            bridge.Graph.Nodes.Values,
            node => (node is VAEDecodeNode or VAEDecodeTiledNode or VAEEncodeNode)
                && ReachesUpstream(bridge, node, samplerNodes[0].Id)
                && ReachesUpstream(
                    bridge,
                    samplerNodes[1].LatentImage.Connection!.Node,
                    node.Id));

        WorkflowNode finalVideoDecode = AsWorkflowNode(
            RequireTypedNode<VAEDecodeTiledNode>(bridge, "202"),
            workflow);
        RequireRetargetedSeparateNode(workflow, finalVideoDecode);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("202", 0)));
    }

    [Fact]
    public void Prehandler_pixel_wrapper_invalidates_the_prepared_latent_handoff()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        string wrapperId = null;
        Action<WorkflowGenerator.ImageToVideoGenInfo> wrapSecondStageSource = info =>
        {
            if (info.ContextID != VideoStagesExtension.SectionIdForStage(1))
            {
                return;
            }

            WorkflowGenerator generator = info.Generator;
            WGNodeData priorMedia = generator.CurrentMedia;
            using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
            ImageScaleNode wrapper = bridge.AddNode(new ImageScaleNode().With(
                Width: priorMedia.Width ?? 512,
                Height: priorMedia.Height ?? 512,
                UpscaleMethod: "lanczos",
                Crop: "disabled"));
            wrapper.Image.TryConnectFromPath(bridge, priorMedia.Path);
            WGNodeData wrappedMedia = priorMedia.WithPath(new JArray(wrapper.Id, 0));
            wrappedMedia.AttachedAudio = priorMedia.AttachedAudio;
            generator.CurrentMedia = wrappedMedia;
            wrapperId = wrapper.Id;
        };
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(wrapSecondStageSource);
        JObject workflow;
        try
        {
            (workflow, WorkflowGenerator unusedGenerator) =
                WorkflowTestHarness.GenerateWithStepsAndState(
                    input,
                    BuildNativeSteps(attachAudioToCurrentMedia: true));
        }
        finally
        {
            Assert.True(WorkflowGenerator.AltImageToVideoPreHandlers.Remove(
                wrapSecondStageSource));
        }

        using WorkflowBridge result = WorkflowBridge.Create(workflow);
        Assert.NotNull(wrapperId);
        SwarmKSamplerNode secondSampler = Assert.Single(
            SamplerNodesOrdered(result),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 44);
        ComfyNode latentInput = secondSampler.LatentImage.Connection!.Node;
        Assert.True(ReachesUpstream(result, latentInput, wrapperId));
        SwarmKSamplerNode firstSampler = Assert.Single(
            SamplerNodesOrdered(result),
            sampler => sampler.FindInput("noise_seed").LiteralAsLong() == 43);
        Assert.Contains(
            result.Graph.Nodes.Values,
            node => node is VAEEncodeNode
                && ReachesUpstream(result, node, firstSampler.Id)
                && ReachesUpstream(result, latentInput, node.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Compatible_multiclip_ltx_stage_chains_decode_only_each_clip_terminal()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject firstClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        firstClip["boundaryOut"] = Constants.BoundaryOutCut;
        JObject secondClip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 12),
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 14));
        string stagesJson = MakeRootConfig(512, 512, firstClip, secondClip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(4, samplers.Count);
        Assert.True(ReachesUpstream(
            bridge,
            samplers[1].LatentImage.Connection!.Node,
            samplers[0].Id));
        Assert.True(ReachesUpstream(
            bridge,
            samplers[3].LatentImage.Connection!.Node,
            samplers[2].Id));
        Assert.False(ReachesUpstream(
            bridge,
            samplers[2].LatentImage.Connection!.Node,
            samplers[0].Id));
        Assert.DoesNotContain(
            bridge.Graph.Nodes.Values,
            node => (node is VAEDecodeNode or VAEDecodeTiledNode or VAEEncodeNode)
                && ReachesUpstream(bridge, node, samplers[0].Id)
                && ReachesUpstream(
                    bridge,
                    samplers[1].LatentImage.Connection!.Node,
                    node.Id));
        Assert.DoesNotContain(
            bridge.Graph.Nodes.Values,
            node => (node is VAEDecodeNode or VAEDecodeTiledNode or VAEEncodeNode)
                && ReachesUpstream(bridge, node, samplers[2].Id)
                && ReachesUpstream(
                    bridge,
                    samplers[3].LatentImage.Connection!.Node,
                    node.Id));

        List<ComfyNode> samplerOutputDecodes = [
            .. bridge.Graph.Nodes.Values.Where(node =>
                (node is VAEDecodeNode or VAEDecodeTiledNode)
                && samplers.Any(sampler => ReachesUpstream(bridge, node, sampler.Id)))
        ];
        Assert.True(
            samplerOutputDecodes.Count == 2,
            "Expected one sampler-output decode per clip; found "
                + string.Join(", ", samplerOutputDecodes.Select(decode =>
                    $"{decode.Id}<-[{string.Join(",", samplers.Where(sampler =>
                        ReachesUpstream(bridge, decode, sampler.Id)).Select(sampler => sampler.Id))}]")));
        Assert.All(samplerOutputDecodes, decode => Assert.True(
            ReachesUpstream(bridge, decode, samplers[1].Id)
            || ReachesUpstream(bridge, decode, samplers[3].Id),
            $"Decode {decode.Id} does not consume a clip-terminal sampler."));
        AssertWorkflowHasNoCycles(workflow);
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
        INodeOutput finalDecodeOutput = bridge.Graph.GetNode<VAEDecodeTiledNode>("202")!.IMAGE;
        Assert.Same(finalDecodeOutput, saveNode.Images.Connection);

        IReadOnlyList<LTXVSeparateAVLatentNode> separateNodes = bridge.Graph.NodesOfType<LTXVSeparateAVLatentNode>();
        Assert.True(separateNodes.Count >= 3);
        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsUseProgressiveAudio(workflow, originalSeparate);

        VAEDecodeTiledNode finalVideoDecode =
            RequireTypedNode<VAEDecodeTiledNode>(bridge, "202");
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
    public void Guide_ref_miss_warns_and_continues_during_workflow_run()
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

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true));

        Assert.NotEmpty(workflow);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("Base2Edit stage 99 does not exist", StringComparison.Ordinal));
    }
}
