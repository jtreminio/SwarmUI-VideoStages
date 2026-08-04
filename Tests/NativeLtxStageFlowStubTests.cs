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
/// The native-LTX stage-flow cases that cannot move to the Comfy API path. Two need generator
/// state the API harness cannot reach — a reset <c>CurrentVae</c> and a published Base2Edit image
/// (no step writes <c>b2e.published.edit.{n}</c>) — one needs a core post-decode wrapper surviving
/// to 11.5, which it does not whenever the extension intercepts the host root, since
/// <c>DropCoreImageToVideoOutput</c> prunes core's whole chain at 11.05 — and two assert warnings
/// on requests that produce no distinguishing graph. Everything else in these files now lives in
/// <see cref="Ltx2StageChainContractTests"/>.
/// </summary>
public partial class StageFlowTests
{
    private static List<string> RequestWarnings(T2IParamInput input) =>
        Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]);

    [Fact]
    public void Native_ltx_generated_reference_reuses_current_video_chain_even_if_current_vae_was_reset()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithCurrentVaeMismatch(models.BaseModel, attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        StageRefStore store = new(generator);
        Assert.Equal(T2IModelClassSorter.CompatLtxv2.ID, store.Generated.Vae.Compat?.ID);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVPreprocessNode>());
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
    }

    [Fact]
    public void Native_ltx_stage_can_use_base2edit_edit_stage_as_clip_ref_image()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage = MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10);
        stage["refStrengths"] = new JArray(0.35);
        string stagesJson = new JArray(
            MakeClipWithRefs(refs: [MakeRef("edit0", frame: 1)], stage)
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithPublishedBase2EditImage(0, attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVPreprocessNode preprocessNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().OrderBy(node => int.Parse(node.Id)));
        ImageScaleNode preprocessUpstream = (ImageScaleNode)preprocessNode.Image.Connection!.Node;
        Assert.Equal("60", preprocessUpstream.Image.Connection!.Node.Id);
        Assert.Equal(0, preprocessUpstream.Image.Connection.SlotIndex);
        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>().OrderBy(node => int.Parse(node.Id)));
        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);
        Assert.Equal(0.35, imgToVideoNode.Strength.LiteralAsDouble());
    }

    [Fact]
    public void Missing_base2edit_edit_stage_reference_warns_and_continues()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "edit0", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: false));

        Assert.NotEmpty(workflow);
        Assert.Contains(
            RequestWarnings(generator.UserInput),
            warning => warning.Contains("Base2Edit stage 0 does not exist", StringComparison.Ordinal));
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

    [Fact]
    public void Native_ltx_wrapper_chain_reuses_decode_audio_and_save_without_duplicate_trim()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(input, BuildNativeStepsWithTrimWrapper(attachAudioToCurrentMedia: false));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        StageRefStore store = new(generator);

        SwarmSaveAnimationWSNode saveNode = Assert.Single(bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal("9", saveNode.Id);
        Assert.Equal("204", saveNode.Images.Connection!.Node.Id);
        Assert.Equal(0, saveNode.Images.Connection.SlotIndex);

        SwarmTrimFramesNode trimNode = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>().OrderBy(node => int.Parse(node.Id)));
        Assert.Equal("204", trimNode.Id);

        ImageFromBatchNode imageFromBatchNode = Assert.Single(
            bridge.Graph.NodesOfType<ImageFromBatchNode>().OrderBy(node => int.Parse(node.Id)));
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(imageFromBatchNode.Image.Connection!),
            store.Generated);
        Assert.NotEqual("204", imageFromBatchNode.Image.Connection!.Node.Id);

        LTXVPreprocessNode preprocessNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVPreprocessNode>().OrderBy(node => int.Parse(node.Id)));
        AssertGuideReferenceResolvesToPreprocessInput(
            workflow,
            WorkflowBridge.ToPath(preprocessNode.Image.Connection!),
            store.Generated);

        LTXVImgToVideoInplaceNode imgToVideoNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>().OrderBy(node => int.Parse(node.Id)));
        Assert.Same(preprocessNode.OutputImage, imgToVideoNode.Image.Connection);

        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        AssertSamplerConsumesImgToVideoOutput(workflow, AsWorkflowNode(imgToVideoNode, workflow), AsWorkflowNode(sampler, workflow));
        WorkflowNode conditioningNode = Assert.Single(AssertLtxConditioningUsesAdvancedEncoders(workflow));
        AssertSamplerUsesConditioningNode(sampler, conditioningNode.Id);

        WorkflowNode originalSeparate = RequireOriginalNativeLtxSeparate(workflow);
        AssertStageLtxConcatsReuseOriginalAudio(workflow, originalSeparate);

        VAEDecodeTiledNode finalVideoDecode =
            RequireTypedNode<VAEDecodeTiledNode>(bridge, "202");
        WorkflowNode finalSeparate = RequireRetargetedSeparateNode(workflow, AsWorkflowNode(finalVideoDecode, workflow));

        LTXVAudioVAEDecodeNode finalAudioDecode = RequireTypedNode<LTXVAudioVAEDecodeNode>(bridge, "203");
        LTXVSeparateAVLatentNode finalSeparateTyped = RequireTypedNode<LTXVSeparateAVLatentNode>(bridge, finalSeparate.Id);
        Assert.Same(finalSeparateTyped.AudioLatent, finalAudioDecode.Samples.Connection);
        AssertNoDanglingTiledVaeDecodes(workflow);
        AssertWorkflowHasNoCycles(workflow);

        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
        Assert.True(JToken.DeepEquals(generator.CurrentMedia.Path, new JArray("204", 0)));
    }
}
