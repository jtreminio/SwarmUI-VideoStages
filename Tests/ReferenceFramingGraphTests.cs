using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using VideoStages.Generated;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public sealed class ReferenceFramingGraphTests
{
    [Theory]
    [InlineData(ReferenceFramingMode.Crop, "center")]
    [InlineData(ReferenceFramingMode.Stretch, "disabled")]
    public void Crop_and_stretch_use_the_expected_core_scale_shape(
        ReferenceFramingMode method,
        string expectedCrop)
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode source = bridge.AddStub("UnitTest_Image", "100")
            .WithOutputs(WGNodeData.DT_IMAGE);

        JArray output = ReferenceFramingGraph.Frame(
            bridge,
            source.GetOutput(0).ToPath(),
            768,
            512,
            method);

        ImageScaleNode scale = Assert.IsType<ImageScaleNode>(bridge.NodeAt(output));
        Assert.Equal("lanczos", scale.UpscaleMethod.LiteralAsString());
        Assert.Equal(expectedCrop, scale.Crop.LiteralAsString());
        Assert.Equal(768, scale.Width.LiteralAsInt());
        Assert.Equal(512, scale.Height.LiteralAsInt());
        Assert.Same(source, scale.Image.Connection?.Node);
        Assert.Empty(bridge.Graph.NodesOfType<SwarmFrameImageNode>());
    }

    [Theory]
    [InlineData(ReferenceFramingMode.Fit, "fit")]
    [InlineData(ReferenceFramingMode.FitGreen, "fit-green")]
    public void Padded_modes_use_the_runtime_frame_node(
        ReferenceFramingMode method,
        string expectedWireMethod)
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode source = bridge.AddStub("UnitTest_Image", "100")
            .WithOutputs(WGNodeData.DT_IMAGE);

        JArray output = ReferenceFramingGraph.Frame(
            bridge,
            source.GetOutput(0).ToPath(),
            768,
            512,
            method);

        SwarmFrameImageNode frame =
            Assert.IsType<SwarmFrameImageNode>(bridge.NodeAt(output));
        Assert.Equal(expectedWireMethod, frame.Method.LiteralAsString());
        Assert.Equal(768, frame.Width.LiteralAsInt());
        Assert.Equal(512, frame.Height.LiteralAsInt());
        Assert.Same(source, frame.ImagesInput.Connection?.Node);
        Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());
    }

    [Fact]
    public void Reuse_keys_include_the_framing_method()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        UnknownNode source = bridge.AddStub("UnitTest_Image", "100")
            .WithOutputs(WGNodeData.DT_IMAGE);
        JArray sourcePath = source.GetOutput(0).ToPath();

        JArray firstCrop = ReferenceFramingGraph.Frame(
            bridge,
            sourcePath,
            768,
            512,
            ReferenceFramingMode.Crop);
        JArray secondCrop = ReferenceFramingGraph.Frame(
            bridge,
            sourcePath,
            768,
            512,
            ReferenceFramingMode.Crop);
        JArray stretch = ReferenceFramingGraph.Frame(
            bridge,
            sourcePath,
            768,
            512,
            ReferenceFramingMode.Stretch);
        JArray fit = ReferenceFramingGraph.Frame(
            bridge,
            sourcePath,
            768,
            512,
            ReferenceFramingMode.Fit);
        JArray fitGreen = ReferenceFramingGraph.Frame(
            bridge,
            sourcePath,
            768,
            512,
            ReferenceFramingMode.FitGreen);

        Assert.True(JToken.DeepEquals(firstCrop, secondCrop));
        Assert.False(JToken.DeepEquals(firstCrop, stretch));
        Assert.False(JToken.DeepEquals(fit, fitGreen));
        Assert.Equal(2, bridge.Graph.NodesOfType<ImageScaleNode>().Count());
        Assert.Equal(2, bridge.Graph.NodesOfType<SwarmFrameImageNode>().Count());
    }
}
