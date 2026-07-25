using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

/// <summary>Captures and resolves the audio output paired with a core video ControlNet input.</summary>
internal sealed class ControlNetAudioCapture(WorkflowGenerator g)
{
    internal static void CaptureUpstreamAudio(
        WorkflowGenerator g,
        WorkflowBridge bridge,
        JArray controlImage,
        int index)
    {
        ComfyNode startNode = bridge.NodeAt(controlImage);
        GetVideoComponentsNode components = startNode as GetVideoComponentsNode
            ?? bridge.Graph.FindNearestUpstream<GetVideoComponentsNode>(startNode);
        if (components is null)
        {
            VideoGraphHelpers.RemoveCached(g, ControlNetCaptureKeys.Audio(index));
            return;
        }
        VideoGraphHelpers.CachePath(
            g,
            ControlNetCaptureKeys.Audio(index),
            WorkflowBridge.ToPath(components.Audio));
    }

    public bool TryGetCapturedAudio(int index, out WGNodeData audio)
    {
        audio = null;
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        if (!ControlNetCaptureKeys.IsValidIndex(index)
            || !VideoGraphHelpers.TryGetCachedPath(
                g, bridge, ControlNetCaptureKeys.Audio(index), out JArray path)
            || bridge.ResolvePath(path) is not INodeOutput output)
        {
            return false;
        }
        audio = output.ToWGNodeData(
            g,
            WGNodeData.DT_AUDIO,
            g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
        return true;
    }
}
