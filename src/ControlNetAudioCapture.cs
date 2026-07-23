using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
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
            g.NodeHelpers.Remove(ControlNetCaptureKeys.Audio(index));
            return;
        }
        g.NodeHelpers[ControlNetCaptureKeys.Audio(index)] =
            WorkflowBridge.ToPath(components.Audio).ToString(Formatting.None);
    }

    public bool TryGetCapturedAudio(int index, out WGNodeData audio)
    {
        audio = null;
        if (!ControlNetCaptureKeys.IsValidIndex(index)
            || !g.NodeHelpers.TryGetValue(ControlNetCaptureKeys.Audio(index), out string encoded)
            || string.IsNullOrWhiteSpace(encoded)
            || JToken.Parse(encoded) is not JArray { Count: 2 } path)
        {
            return false;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        INodeOutput output = bridge.ResolvePath(path);
        if (output is null)
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
