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
        if (rawAudioPath is not JArray rawRef || rawRef.Count != 2)
        {
            return rawAudioPath;
        }

        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node
                || !StringUtils.NodeTypeMatches(node, NodeTypes.SwarmEnsureAudio)
                || node["inputs"] is not JObject inputs
                || inputs["audio"] is not JArray audioInput
                || audioInput.Count != 2)
            {
                continue;
            }
            if (WorkflowConnectionRefsEqual(audioInput, rawRef))
            {
                return new JArray(property.Name, 0);
            }
        }

        if (!IsSwarmLoadAudioB64Output(g, rawRef))
        {
            return rawAudioPath;
        }

        JObject ensureInputs = new()
        {
            ["audio"] = rawRef,
            ["target_duration"] = 0.1
        };
        string ensured = swarmEnsureAudioStableNodeId is null
            ? g.CreateNode(NodeTypes.SwarmEnsureAudio, ensureInputs)
            : g.CreateNode(NodeTypes.SwarmEnsureAudio, ensureInputs, swarmEnsureAudioStableNodeId);
        return new JArray(ensured, 0);
    }

    public static bool IsSwarmLoadAudioB64Output(WorkflowGenerator g, JArray rawRef)
    {
        string sourceId = $"{rawRef[0]}";
        if (!g.Workflow.TryGetValue(sourceId, out JToken token) || token is not JObject node)
        {
            return false;
        }

        return StringUtils.NodeTypeMatches(node, NodeTypes.SwarmLoadAudioB64);
    }

    public static bool WorkflowConnectionRefsEqual(JToken left, JToken right)
    {
        if (left is not JArray la || right is not JArray ra || la.Count != 2 || ra.Count != 2)
        {
            return false;
        }

        return $"{la[0]}" == $"{ra[0]}" && la[1].Value<int>() == ra[1].Value<int>();
    }
}
