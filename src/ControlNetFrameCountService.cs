using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>Builds and caches the frame-count path for a captured full ControlNet video image.</summary>
internal sealed class ControlNetFrameCountService(WorkflowGenerator g)
{
    public bool TryCreate(int index, out JArray framesConnection)
    {
        framesConnection = null;
        if (!ControlNetCaptureKeys.IsValidIndex(index))
        {
            return false;
        }
        string helperKey = ControlNetCaptureKeys.FrameCount(index);
        if (VideoGraphHelpers.TryGetCachedPath(g, bridge: null, helperKey, out JArray cached))
        {
            framesConnection = cached;
            return true;
        }
        if (!ControlNetCoreMediaCapture.TryGetCapturedControlImage(g, index, out WGNodeData controlImage)
            || controlImage.Path is not JArray { Count: 2 } controlImagePath)
        {
            return false;
        }

        using WorkflowBridge bridge = BridgeSync.For(g);
        ResizeImageMaskNodeNode upstreamResize =
            ControlNetCoreMediaCapture.FindUpstreamScaleToMultipleResize(bridge, controlImagePath);
        INodeOutput frameSource = upstreamResize?.Resized
            ?? bridge.ResolvePath(ControlNetCoreMediaCapture.PeelSingleFrameWrap(bridge, controlImagePath));
        if (frameSource is null
            || !ControlNetCoreMediaCapture.HasVideoUpstream(bridge, WorkflowBridge.ToPath(frameSource)))
        {
            return false;
        }
        GetImageSizeNode sizeNode = bridge.AddNode(new GetImageSizeNode());
        sizeNode.Image.ConnectToUntyped(frameSource);
        bridge.SyncNode(sizeNode);
        framesConnection = WorkflowBridge.ToPath(sizeNode.BatchSize);
        VideoGraphHelpers.CachePath(g, helperKey, framesConnection);
        return true;
    }
}
