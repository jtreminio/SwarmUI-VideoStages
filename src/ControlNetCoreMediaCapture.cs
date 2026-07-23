using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

/// <summary>Captures each active core video ControlNet's full image path and normalizes it for LTX.</summary>
internal sealed class ControlNetCoreMediaCapture(WorkflowGenerator g)
{
    public void Capture()
    {
        using WorkflowBridge bridge = BridgeSync.For(g);
        ControlNetGraphDiscovery discovery = new(g);
        HashSet<string> usedApplyNodes = [];
        for (int index = 0; index < T2IParamTypes.Controlnets.Length; index++)
        {
            T2IParamTypes.ControlNetParamHolder parameters = T2IParamTypes.Controlnets[index];
            if (parameters is null
                || !g.UserInput.TryGet(parameters.Strength, out double _)
                || !g.UserInput.TryGet(parameters.Model, out T2IModel model)
                || !discovery.TryFindCoreApply(
                    bridge, model, usedApplyNodes, out (string Id, JObject Node) applyNode, out JArray controlImage)
                || !HasVideoUpstream(bridge, controlImage))
            {
                g.NodeHelpers.Remove(ControlNetCaptureKeys.Image(index));
                g.NodeHelpers.Remove(ControlNetCaptureKeys.Audio(index));
                continue;
            }

            EnsureResizeMultiple(bridge, controlImage);
            g.NodeHelpers[ControlNetCaptureKeys.Image(index)] =
                new JArray(controlImage[0], controlImage[1]).ToString(Formatting.None);
            ControlNetAudioCapture.CaptureUpstreamAudio(g, bridge, controlImage, index);
            EnsureSingleFrameWrap(bridge, controlImage);
            usedApplyNodes.Add(applyNode.Id);
        }
    }

    internal static bool TryGetCapturedControlImage(
        WorkflowGenerator g,
        int index,
        out WGNodeData controlImage)
    {
        controlImage = null;
        if (!ControlNetCaptureKeys.IsValidIndex(index)
            || !g.NodeHelpers.TryGetValue(ControlNetCaptureKeys.Image(index), out string encoded)
            || string.IsNullOrWhiteSpace(encoded)
            || JToken.Parse(encoded) is not JArray { Count: 2 } path)
        {
            return false;
        }
        controlImage = new WGNodeData(path, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        return true;
    }

    internal static JArray PeelSingleFrameWrap(WorkflowBridge bridge, JArray imagePath)
    {
        if (bridge.NodeAt<ImageFromBatchNode>(imagePath) is ImageFromBatchNode batch
            && batch.Length.LiteralAsInt() == 1
            && batch.Image.Connection is INodeOutput imageIn)
        {
            return WorkflowBridge.ToPath(imageIn);
        }
        return new JArray(imagePath[0], imagePath[1]);
    }

    internal static ResizeImageMaskNodeNode FindUpstreamScaleToMultipleResize(
        WorkflowBridge bridge,
        JArray startRef)
    {
        if (bridge.ResolvePath(startRef)?.Node is not ComfyNode start)
        {
            return null;
        }
        return (IsScaleToMultipleResize(start)
            ? start
            : bridge.Graph.FindNearestUpstream(start, IsScaleToMultipleResize))
            as ResizeImageMaskNodeNode;
    }

    internal static bool HasVideoUpstream(WorkflowBridge bridge, JArray outputRef)
    {
        if (outputRef is not { Count: 2 } || bridge.NodeAt(outputRef) is not ComfyNode start)
        {
            return false;
        }
        Queue<ComfyNode> pending = new();
        HashSet<string> visited = [start.Id];
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            ComfyNode current = pending.Dequeue();
            if (current is SwarmLoadVideoB64Node or GetVideoComponentsNode)
            {
                return true;
            }
            foreach (INodeInput input in current.Inputs)
            {
                if (input.Connection?.Node is ComfyNode upstream && visited.Add(upstream.Id))
                {
                    pending.Enqueue(upstream);
                }
            }
        }
        return false;
    }

    private static void EnsureResizeMultiple(WorkflowBridge bridge, JArray controlImage)
    {
        if (FindUpstreamScaleToMultipleResize(bridge, controlImage) is ResizeImageMaskNodeNode existing)
        {
            existing.ExtraInputs["resize_type.multiple"] = 64;
            bridge.SyncNode(existing);
            return;
        }
        if (bridge.ResolvePath(controlImage) is not INodeOutput consumerOutput)
        {
            return;
        }
        if (consumerOutput.Node is ImageFromBatchNode batch
            && batch.Length.LiteralAsInt() == 1
            && batch.Image.Connection is INodeOutput batchSource)
        {
            ResizeImageMaskNodeNode rewired = bridge.AddNode(new ResizeImageMaskNodeNode()).With(
                ResizeType: "scale to multiple",
                ScaleMethod: "lanczos");
            rewired.Input.ConnectToUntyped(batchSource);
            rewired.ExtraInputs["resize_type.multiple"] = 64;
            batch.Image.ConnectToUntyped(rewired.Resized);
            bridge.SyncNode(rewired);
            bridge.SyncNode(batch);
            return;
        }
        ResizeImageMaskNodeNode resize = bridge.AddNode(new ResizeImageMaskNodeNode()).With(
            ResizeType: "scale to multiple",
            ScaleMethod: "lanczos");
        resize.Input.ConnectToUntyped(consumerOutput);
        resize.ExtraInputs["resize_type.multiple"] = 64;
        bridge.SyncNode(resize);
        controlImage[0] = resize.Id;
        controlImage[1] = 0;
    }

    private static void EnsureSingleFrameWrap(WorkflowBridge bridge, JArray controlImage)
    {
        if (bridge.NodeAt(controlImage) is ImageFromBatchNode)
        {
            return;
        }
        ImageFromBatchNode batch = bridge.AddNode(new ImageFromBatchNode()).With(BatchIndex: 0, Length: 1);
        batch.Image.TryConnectFromPath(bridge, controlImage);
        bridge.SyncNode(batch);
        controlImage[0] = batch.Id;
        controlImage[1] = 0;
    }

    private static bool IsScaleToMultipleResize(ComfyNode node) =>
        node is ResizeImageMaskNodeNode resize
        && resize.ResizeType.LiteralAsString() == "scale to multiple";
}
