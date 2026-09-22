using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution.Graph;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class LtxPreprocessInputsTests
{
    [Theory]
    [InlineData(18)]
    [InlineData(25)]
    public void Compressed_preprocessing_preserves_shared_host_alpha_and_invalidates_its_cache(int compression)
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = new() { UserInput = new T2IParamInput(null), Workflow = [] };
        string preprocessId;
        string framingId;
        Dictionary<string, JToken> hostNodes;
        using (WorkflowBridge bridge = BridgeSync.For(generator))
        {
            LoadImageNode upload = bridge.AddNode(new LoadImageNode());
            JoinImageWithAlphaNode rgba = bridge.AddNode(new JoinImageWithAlphaNode());
            rgba.Image.ConnectTo(upload.IMAGE);
            rgba.Alpha.ConnectTo(upload.MASK);
            ImageScaleNode framing = bridge.AddNode(new ImageScaleNode());
            framing.Image.ConnectTo(rgba.IMAGE);
            framingId = framing.Id;
            ImageScaleNode hostConsumer = bridge.AddNode(new ImageScaleNode());
            hostConsumer.Image.ConnectTo(framing.IMAGE);
            hostNodes = generator.Workflow.Properties()
                .ToDictionary(node => node.Name, node => node.Value.DeepClone());
            LTXVPreprocessNode preprocess = bridge.AddNode(new LTXVPreprocessNode().With(ImgCompression: compression));
            preprocess.Image.ConnectTo(framing.IMAGE);
            preprocessId = preprocess.Id;
        }
        generator.NodeHelpers["old_preprocess"] = preprocessId;
        generator.NodeHelpers["host_framing"] = framingId;

        LtxPreprocessInputs.EnsureRgb(generator);
        LtxPreprocessInputs.EnsureRgb(generator);

        using WorkflowBridge result = WorkflowBridge.Create(generator.Workflow);
        SplitImageWithAlphaNode rgb = Assert.Single(result.Graph.NodesOfType<SplitImageWithAlphaNode>());
        Assert.Same(result.Graph.GetNode<ImageScaleNode>(framingId).IMAGE, rgb.Image.Connection);
        Assert.Same(rgb.IMAGE, result.Graph.GetNode<LTXVPreprocessNode>(preprocessId).Image.Connection);
        Assert.All(hostNodes, node => Assert.True(JToken.DeepEquals(node.Value, generator.Workflow[node.Key])));
        Assert.False(generator.NodeHelpers.ContainsKey("old_preprocess"));
        Assert.Equal(framingId, generator.NodeHelpers["host_framing"]);
        AssertAcyclic(result);
    }

    [Fact]
    public void Bypassed_preprocessing_keeps_rgba_and_its_cache()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = new() { UserInput = new T2IParamInput(null), Workflow = [] };
        using (WorkflowBridge bridge = BridgeSync.For(generator))
        {
            JoinImageWithAlphaNode rgba = bridge.AddNode(new JoinImageWithAlphaNode());
            LTXVPreprocessNode preprocess = bridge.AddNode(new LTXVPreprocessNode().With(ImgCompression: 0));
            preprocess.Image.ConnectTo(rgba.IMAGE);
            generator.NodeHelpers["bypassed_preprocess"] = preprocess.Id;
        }
        JObject original = (JObject)generator.Workflow.DeepClone();

        LtxPreprocessInputs.EnsureRgb(generator);

        Assert.True(JToken.DeepEquals(original, generator.Workflow));
        Assert.True(generator.NodeHelpers.ContainsKey("bypassed_preprocess"));
    }

    [Fact]
    public void Reuses_existing_rgb_output_and_leaves_normalized_preprocessors_unchanged()
    {
        using SwarmUiTestContext _ = new();
        WorkflowGenerator generator = new() { UserInput = new T2IParamInput(null), Workflow = [] };
        string splitId;
        using (WorkflowBridge bridge = BridgeSync.For(generator))
        {
            JoinImageWithAlphaNode rgba = bridge.AddNode(new JoinImageWithAlphaNode());
            SplitImageWithAlphaNode split = bridge.AddNode(new SplitImageWithAlphaNode());
            split.Image.ConnectTo(rgba.IMAGE);
            splitId = split.Id;
            LTXVPreprocessNode normalized = bridge.AddNode(new LTXVPreprocessNode().With(ImgCompression: 18));
            normalized.Image.ConnectTo(split.IMAGE);
            generator.NodeHelpers["normalized_preprocess"] = normalized.Id;
            LTXVPreprocessNode added = bridge.AddNode(new LTXVPreprocessNode().With(ImgCompression: 25));
            added.Image.ConnectTo(rgba.IMAGE);
        }

        LtxPreprocessInputs.EnsureRgb(generator);
        LtxPreprocessInputs.EnsureRgb(generator);

        using WorkflowBridge result = WorkflowBridge.Create(generator.Workflow);
        SplitImageWithAlphaNode rgb = Assert.Single(result.Graph.NodesOfType<SplitImageWithAlphaNode>());
        Assert.Equal(splitId, rgb.Id);
        Assert.All(result.Graph.NodesOfType<LTXVPreprocessNode>(), preprocess =>
            Assert.Same(rgb.IMAGE, preprocess.Image.Connection));
        Assert.True(generator.NodeHelpers.ContainsKey("normalized_preprocess"));
        AssertAcyclic(result);
    }
}
