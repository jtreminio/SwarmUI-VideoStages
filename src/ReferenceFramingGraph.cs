using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using VideoStages.Generated;

namespace VideoStages;

/// <summary>Builds and reuses the graph branch for one clip's reference-framing policy.</summary>
internal static class ReferenceFramingGraph
{
    private const string UpscaleMethod = "lanczos";

    internal static JArray Frame(
        WorkflowBridge bridge,
        JArray imagePath,
        int targetWidth,
        int targetHeight,
        ReferenceFramingMode method,
        bool unwrapExistingFraming = true)
    {
        if (imagePath is not { Count: 2 })
        {
            return imagePath;
        }

        int width = Math.Max(2, targetWidth);
        int height = Math.Max(2, targetHeight);
        if (IsExactFrame(bridge, imagePath, width, height, method))
        {
            return imagePath;
        }

        JArray sourcePath = unwrapExistingFraming
            ? ResolveBaseSource(bridge, imagePath)
            : imagePath;
        if (method is ReferenceFramingMode.Crop or ReferenceFramingMode.Stretch)
        {
            string crop = method == ReferenceFramingMode.Crop ? "center" : "disabled";
            ImageScaleNode reusable = bridge.Graph.NodesOfType<ImageScaleNode>().FirstOrDefault(
                candidate =>
                    HasSource(candidate.Image, sourcePath)
                    && candidate.Width.LiteralAsInt() == width
                    && candidate.Height.LiteralAsInt() == height
                    && candidate.UpscaleMethod.LiteralAsString() == UpscaleMethod
                    && candidate.Crop.LiteralAsString() == crop);
            if (reusable is not null)
            {
                return reusable.IMAGE.ToPath();
            }

            return ImageScaleReuse.Create(
                    bridge,
                    sourcePath,
                    width,
                    height,
                    crop,
                    UpscaleMethod)
                .IMAGE
                .ToPath();
        }

        string wireMethod = ReferenceFraming.ToWireValue(method);
        SwarmFrameImageNode existing = bridge.Graph
            .NodesOfType<SwarmFrameImageNode>()
            .FirstOrDefault(candidate =>
                HasSource(candidate.ImagesInput, sourcePath)
                && candidate.Width.LiteralAsInt() == width
                && candidate.Height.LiteralAsInt() == height
                && candidate.Method.LiteralAsString() == wireMethod);
        if (existing is not null)
        {
            return existing.Images.ToPath();
        }

        SwarmFrameImageNode frame = bridge.AddNode(new SwarmFrameImageNode().With(
            Width: width,
            Height: height,
            Method: wireMethod));
        frame.ImagesInput.ConnectFromPath(bridge, sourcePath);
        return frame.Images.ToPath();
    }

    private static bool IsExactFrame(
        WorkflowBridge bridge,
        JArray imagePath,
        int width,
        int height,
        ReferenceFramingMode method)
    {
        if (method is ReferenceFramingMode.Crop or ReferenceFramingMode.Stretch)
        {
            string crop = method == ReferenceFramingMode.Crop ? "center" : "disabled";
            return bridge.NodeAt<ImageScaleNode>(imagePath) is ImageScaleNode scale
                && scale.Width.LiteralAsInt() == width
                && scale.Height.LiteralAsInt() == height
                && scale.UpscaleMethod.LiteralAsString() == UpscaleMethod
                && scale.Crop.LiteralAsString() == crop;
        }

        return bridge.NodeAt<SwarmFrameImageNode>(imagePath) is SwarmFrameImageNode frame
            && frame.Width.LiteralAsInt() == width
            && frame.Height.LiteralAsInt() == height
            && frame.Method.LiteralAsString() == ReferenceFraming.ToWireValue(method);
    }

    private static bool HasSource(INodeInput input, JArray sourcePath) =>
        input.Connection is INodeOutput source
        && sourcePath is { Count: 2 }
        && source.Node.Id == $"{sourcePath[0]}"
        && source.SlotIndex == (int)sourcePath[1];

    private static JArray ResolveBaseSource(WorkflowBridge bridge, JArray imagePath)
    {
        if (NodeRef.From(imagePath) is not { } start)
        {
            return imagePath;
        }

        ComfyNode current = bridge.Graph.GetNode(start.NodeId);
        int currentSlot = start.SlotIndex;
        HashSet<string> visited = [];
        while (current is not null && visited.Add($"{current.Id}::{currentSlot}"))
        {
            INodeOutput upstream = current switch
            {
                ImageScaleNode scale => scale.Image.Connection,
                SwarmFrameImageNode frame => frame.ImagesInput.Connection,
                _ => null,
            };
            if (upstream is null)
            {
                break;
            }
            current = upstream.Node;
            currentSlot = upstream.SlotIndex;
        }
        return current is null
            ? imagePath
            : new NodeRef(current.Id, currentSlot).ToJArray();
    }
}
