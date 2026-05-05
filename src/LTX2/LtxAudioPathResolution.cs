using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.LTX2;

internal static class LtxAudioPathResolution
{
    public static JToken ResolveLengthToFramesAudioSource(
        WorkflowGenerator g,
        JToken rawAudioPath,
        string swarmEnsureAudioStableNodeId)
    {
        if (rawAudioPath is not JArray { Count: 2 } rawRef)
        {
            return rawAudioPath;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
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
        BridgeSync.SyncLastId(g);
        return new JArray(ensure.Id, 0);
    }

    public static bool IsSwarmLoadAudioB64Output(WorkflowGenerator g, JArray rawRef)
    {
        if (rawRef is not { Count: 2 })
        {
            return false;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return bridge.Graph.GetNode($"{rawRef[0]}") is SwarmLoadAudioB64Node;
    }

    public static bool WorkflowConnectionRefsEqual(JToken left, JToken right)
    {
        if (left is not JArray { Count: 2 } la || right is not JArray { Count: 2 } ra)
        {
            return false;
        }

        return $"{la[0]}" == $"{ra[0]}" && la[1].Value<int>() == ra[1].Value<int>();
    }
}
