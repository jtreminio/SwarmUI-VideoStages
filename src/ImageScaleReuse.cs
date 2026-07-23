using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;

namespace VideoStages;

/// <summary>Finds an existing ImageScale node so repeated scale requests over the
/// same source reuse one node instead of stacking duplicates in the graph.</summary>
internal static class ImageScaleReuse
{
    /// <summary>True when the graph already scales <paramref name="sourcePath"/> to
    /// exactly (targetW, targetH). <paramref name="mutateMatch"/>, when given, runs on
    /// the match before it is returned (e.g. force center-crop and sync).</summary>
    public static bool TryFind(
        WorkflowBridge bridge,
        JArray sourcePath,
        int targetW,
        int targetH,
        out ImageScaleNode scale,
        Action<ImageScaleNode> mutateMatch = null)
    {
        scale = null;
        if (sourcePath is not { Count: 2 })
        {
            return false;
        }
        string sourceId = $"{sourcePath[0]}";
        int sourceSlot = (int)sourcePath[1];

        foreach (ImageScaleNode candidate in bridge.Graph.NodesOfType<ImageScaleNode>())
        {
            INodeOutput candidateImage = candidate.Image.Connection;
            if (candidateImage?.Node.Id != sourceId
                || candidateImage.SlotIndex != sourceSlot
                || candidate.Width.LiteralAsInt() != targetW
                || candidate.Height.LiteralAsInt() != targetH)
            {
                continue;
            }
            mutateMatch?.Invoke(candidate);
            scale = candidate;
            return true;
        }
        return false;
    }

    /// <summary>Creates an ImageScale node scaling <paramref name="sourcePath"/> to (width, height).
    /// <paramref name="crop"/> and <paramref name="upscaleMethod"/> are per-caller; the crop differs
    /// between call sites (center vs disabled).</summary>
    public static ImageScaleNode Create(
        WorkflowBridge bridge,
        JArray sourcePath,
        int width,
        int height,
        string crop,
        string upscaleMethod = "lanczos")
    {
        ImageScaleNode scale = bridge.AddNode(new ImageScaleNode().With(
            Width: width,
            Height: height,
            UpscaleMethod: upscaleMethod,
            Crop: crop));
        scale.Image.ConnectFromPath(bridge, sourcePath);
        return scale;
    }

    /// <summary>Scales <paramref name="sourcePath"/> to (width, height). When the source is itself an
    /// ImageScale whose output nothing consumes yet, re-fits that node in place — one resample from
    /// the raw source instead of two chained ones (its crop is kept: a conform's center crop frames
    /// the enlarged dims identically since the aspect is preserved). Otherwise creates a new node.</summary>
    public static ImageScaleNode RetargetOrCreate(
        WorkflowBridge bridge,
        JArray sourcePath,
        int width,
        int height,
        string crop,
        string upscaleMethod)
    {
        if (sourcePath is { Count: 2 }
            && bridge.NodeAt(sourcePath) is ImageScaleNode existing
            && bridge.ResolvePath(sourcePath) is INodeOutput output
            && !bridge.Graph.FindDownstream(output).Any())
        {
            existing.With(Width: width, Height: height, UpscaleMethod: upscaleMethod);
            bridge.SyncNode(existing);
            return existing;
        }
        return Create(bridge, sourcePath, width, height, crop, upscaleMethod);
    }

    /// <summary>When <paramref name="sourcePath"/> already points at an ImageScale node, re-fits it to
    /// (targetWidth, targetHeight) with center crop (collapsing a redundant upstream ImageScale) and
    /// returns its id. A second reuse strategy alongside <see cref="TryFind"/>.</summary>
    public static bool TryNormalizeExisting(
        WorkflowBridge bridge,
        JArray sourcePath,
        int targetWidth,
        int targetHeight,
        out string scaleNodeId)
    {
        scaleNodeId = null;
        if (sourcePath is not { Count: 2 }
            || bridge.NodeAt(sourcePath) is not ImageScaleNode scale)
        {
            return false;
        }

        ImageScaleNode collapsed = scale;
        if (scale.Image.Connection?.Node is ImageScaleNode upstream)
        {
            INodeOutput outerOutput = bridge.ResolvePath(sourcePath);
            if (outerOutput is not null && !bridge.Graph.FindDownstream(outerOutput).Any())
            {
                bridge.RemoveNode(scale);
            }
            collapsed = upstream;
        }

        collapsed.With(
            Width: targetWidth,
            Height: targetHeight,
            Crop: "center");
        if (!collapsed.UpscaleMethod.HasValue)
        {
            collapsed.UpscaleMethod.Set("lanczos");
        }
        bridge.SyncNode(collapsed);
        scaleNodeId = collapsed.Id;
        return true;
    }
}
