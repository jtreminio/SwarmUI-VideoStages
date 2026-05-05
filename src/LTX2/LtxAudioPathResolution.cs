using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;

namespace VideoStages.LTX2;

internal static class LtxAudioPathResolution
{
    public static JToken ResolveLengthToFramesAudioSource(
        WorkflowBridge bridge,
        JToken rawAudioPath,
        string swarmEnsureAudioStableNodeId)
    {
        if (rawAudioPath is not JArray { Count: 2 } rawRef)
        {
            return rawAudioPath;
        }

        string rawNodeId = $"{rawRef[0]}";
        int rawSlot = (int)rawRef[1];
        foreach (SwarmEnsureAudioNode existing in bridge.Graph.NodesOfType<SwarmEnsureAudioNode>())
        {
            if (existing.Audio.Connection is INodeOutput audioConn
                && audioConn.Node.Id == rawNodeId
                && audioConn.SlotIndex == rawSlot)
            {
                return new JArray(existing.Id, 0);
            }
        }

        if (bridge.Graph.GetNode(rawNodeId) is not SwarmLoadAudioB64Node)
        {
            return rawAudioPath;
        }

        SwarmEnsureAudioNode ensure = swarmEnsureAudioStableNodeId is null
            ? bridge.AddNode(new SwarmEnsureAudioNode())
            : bridge.AddNode(new SwarmEnsureAudioNode(), swarmEnsureAudioStableNodeId);
        if (bridge.ResolvePath(rawRef) is INodeOutput rawSource)
        {
            ensure.Audio.ConnectToUntyped(rawSource);
        }
        ensure.TargetDuration.Set(0.1);
        bridge.SyncNode(ensure);
        return new JArray(ensure.Id, 0);
    }
}
