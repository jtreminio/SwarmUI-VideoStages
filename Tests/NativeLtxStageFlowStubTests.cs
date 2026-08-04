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
/// The one native-LTX stage-flow case that cannot move to the Comfy API path: it needs a core
/// post-decode wrapper still standing when the stage runner reaches 11.5, and no POST produces
/// that — whenever the extension intercepts the host root,
/// <c>DropCoreImageToVideoOutput</c> prunes core's whole chain at 11.05. Everything else in these
/// files now lives in <see cref="Ltx2StageChainContractTests"/>.
/// </summary>
public partial class StageFlowTests
{
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
