using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Derives the LTX-specific ControlNet media branch from the architecture-neutral host capture.
/// The full normalized batch is cached before the core apply input is wrapped to one frame.
/// </summary>
internal sealed class LtxControlNetMediaNormalizer(WorkflowGenerator g)
{
    private readonly LtxRuntimeKeyScope _keys = new();

    internal void Normalize()
    {
        if (!g.NodeHelpers.TryAdd(_keys.ControlNetNormalized, "normalized"))
        {
            return;
        }
        using WorkflowBridge bridge = BridgeSync.For(g);
        for (int index = 0; index <= 2; index++)
        {
            if (!TryGetRawApplyImage(bridge, index, out JArray controlImage))
            {
                Clear(index);
                continue;
            }
            JArray normalized = EnsureResizeMultiple(bridge, controlImage);
            VideoGraphHelpers.CachePath(
                g,
                ImageKey(index),
                normalized);
            EnsureSingleFrameWrap(bridge, controlImage, normalized);
        }
    }

    internal bool TryGetNormalizedControlImage(
        int index,
        out WGNodeData controlImage)
    {
        controlImage = null;
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (!ControlNetCaptureKeys.IsValidIndex(index)
            || !VideoGraphHelpers.TryGetCachedPath(
                g,
                bridge,
                ImageKey(index),
                out JArray path))
        {
            return false;
        }
        controlImage = new WGNodeData(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        return true;
    }

    internal bool TryCreateFrameCount(int index, out JArray framesConnection)
    {
        framesConnection = null;
        if (!ControlNetCaptureKeys.IsValidIndex(index))
        {
            return false;
        }
        string helperKey = FrameCountKey(index);
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge: null, helperKey, out JArray cached))
        {
            framesConnection = cached;
            return true;
        }
        if (!TryGetNormalizedControlImage(index, out WGNodeData controlImage)
            || controlImage.Path is not JArray { Count: 2 } controlImagePath)
        {
            return false;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput frameSource = bridge.ResolvePath(controlImagePath);
        if (frameSource is null
            || !ControlNetCoreMediaCapture.HasVideoUpstream(
                bridge,
                WorkflowBridge.ToPath(frameSource)))
        {
            return false;
        }
        GetImageSizeNode sizeNode = bridge.AddNode(new GetImageSizeNode());
        sizeNode.Image.ConnectToUntyped(frameSource);
        framesConnection = WorkflowBridge.ToPath(sizeNode.BatchSize);
        VideoGraphHelpers.CachePath(g, helperKey, framesConnection);
        return true;
    }

    internal static JArray PeelSingleFrameWrap(
        WorkflowBridge bridge,
        JArray imagePath)
    {
        if (bridge.NodeAt<ImageFromBatchNode>(imagePath) is ImageFromBatchNode batch
            && batch.BatchIndex.LiteralAsInt() == 0
            && batch.Length.LiteralAsInt() == 1
            && batch.Image.Connection is INodeOutput imageIn)
        {
            return WorkflowBridge.ToPath(imageIn);
        }
        return new JArray(imagePath[0], imagePath[1]);
    }

    private bool TryGetRawApplyImage(
        WorkflowBridge bridge,
        int index,
        out JArray controlImage)
    {
        controlImage = null;
        return ControlNetCoreMediaCapture.TryGetCapturedControlImage(g, index, out _)
            && g.NodeHelpers.TryGetValue(
                ControlNetCaptureKeys.Apply(index),
                out string applyId)
            && bridge.Graph.GetNode(applyId) is not null
            && g.Workflow[applyId] is JObject applyNode
            && VideoGraphHelpers.TryGetInputRef(
                applyNode,
                "image",
                out controlImage);
    }

    private void Clear(int index)
    {
        VideoGraphHelpers.RemoveCached(g, ImageKey(index));
        VideoGraphHelpers.RemoveCached(g, FrameCountKey(index));
    }

    private string ImageKey(int index) => _keys.ControlNetFullImage(index);

    private string FrameCountKey(int index) => _keys.ControlNetFrameCount(index);

    private static JArray EnsureResizeMultiple(
        WorkflowBridge bridge,
        JArray controlImage)
    {
        INodeOutput source;
        if (bridge.NodeAt<ImageFromBatchNode>(
                controlImage) is ImageFromBatchNode canonicalWrapper
            && canonicalWrapper.BatchIndex.LiteralAsInt() == 0
            && canonicalWrapper.Length.LiteralAsInt() == 1
            && canonicalWrapper.Image.Connection is INodeOutput wrappedSource)
        {
            source = wrappedSource;
        }
        else if (bridge.ResolvePath(controlImage) is INodeOutput directSource)
        {
            source = directSource;
        }
        else
        {
            return new JArray(controlImage[0], controlImage[1]);
        }
        if (source.Node is ResizeImageMaskNodeNode existing
            && IsScaleToMultipleResize(existing)
            && existing.ExtraInputs?["resize_type.multiple"]?.Value<int>() == 64)
        {
            return WorkflowBridge.ToPath(source);
        }
        ResizeImageMaskNodeNode resize =
            bridge.AddNode(new ResizeImageMaskNodeNode()).With(
                ResizeType: "scale to multiple",
                ScaleMethod: "lanczos");
        resize.Input.ConnectToUntyped(source);
        resize.ExtraInputs["resize_type.multiple"] = 64;
        bridge.SyncNode(resize);
        return WorkflowBridge.ToPath(resize.Resized);
    }

    private static void EnsureSingleFrameWrap(
        WorkflowBridge bridge,
        JArray controlImage,
        JArray normalized)
    {
        if (bridge.NodeAt<ImageFromBatchNode>(
                controlImage) is ImageFromBatchNode existing
            && existing.BatchIndex.LiteralAsInt() == 0
            && existing.Length.LiteralAsInt() == 1)
        {
            existing.Image.TryConnectFromPath(bridge, normalized);
            return;
        }
        ImageFromBatchNode batch =
            bridge.AddNode(new ImageFromBatchNode()).With(
                BatchIndex: 0,
                Length: 1);
        batch.Image.TryConnectFromPath(bridge, normalized);
        controlImage[0] = batch.Id;
        controlImage[1] = 0;
    }

    private static bool IsScaleToMultipleResize(ComfyNode node) =>
        node is ResizeImageMaskNodeNode resize
        && resize.ResizeType.LiteralAsString() == "scale to multiple";
}
