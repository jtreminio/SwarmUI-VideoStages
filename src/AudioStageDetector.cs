using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public sealed class AudioStageDetector(WorkflowGenerator g)
{
    public sealed record Detection(
        WGNodeData Audio,
        string MatchedNodeId,
        string MatchedClassType,
        string SourceNodeId,
        int Priority);

    public Detection Detect()
    {
        Detection best = null;

        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node)
            {
                continue;
            }
            string classType = $"{node["class_type"]}";
            Detection candidate = classType switch
            {
                NodeTypes.SwarmSaveAudioWS => BuildSaveCandidate(property.Name, classType, node, 3),
                _ when IsSaveAudioNode(classType) => BuildSaveCandidate(property.Name, classType, node, 2),
                NodeTypes.VAEDecodeAudio => BuildDecodeCandidate(property.Name, classType, 1),
                _ => null,
            };

            if (ShouldReplace(best, candidate))
            {
                best = candidate;
            }
        }

        return best;
    }

    private WGNodeData CreateAudioNode(JArray path) =>
        new(path, g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat ?? g.CurrentCompat());

    private Detection BuildSaveCandidate(
        string nodeId,
        string classType,
        JObject node,
        int priority)
    {
        if (node["inputs"] is not JObject inputs
            || inputs["audio"] is not JArray audioRef
            || audioRef.Count != 2)
        {
            return null;
        }

        string sourceId = $"{audioRef[0]}";
        WGNodeData audio = CreateAudioNode(new JArray(audioRef[0], audioRef[1]));
        return new Detection(audio, nodeId, classType, sourceId, priority);
    }

    private Detection BuildDecodeCandidate(
        string nodeId,
        string classType,
        int priority)
    {
        WGNodeData audio = CreateAudioNode(new JArray(nodeId, 0));
        return new Detection(audio, nodeId, classType, nodeId, priority);
    }

    private static bool IsSaveAudioNode(string classType)
    {
        return !string.IsNullOrWhiteSpace(classType)
            && classType.StartsWith("SaveAudio", StringComparison.Ordinal);
    }

    private static bool ShouldReplace(Detection current, Detection candidate)
    {
        if (candidate is null)
        {
            return false;
        }
        if (current is null)
        {
            return true;
        }
        if (candidate.Priority != current.Priority)
        {
            return candidate.Priority > current.Priority;
        }
        return CompareNodeIds(candidate.MatchedNodeId, current.MatchedNodeId) > 0;
    }

    private static int CompareNodeIds(string left, string right)
    {
        bool hasLeftNumeric = long.TryParse(left, out long leftNumeric);
        bool hasRightNumeric = long.TryParse(right, out long rightNumeric);
        if (hasLeftNumeric && hasRightNumeric)
        {
            return leftNumeric.CompareTo(rightNumeric);
        }
        if (hasLeftNumeric)
        {
            return 1;
        }
        if (hasRightNumeric)
        {
            return -1;
        }
        return string.CompareOrdinal(left, right);
    }
}
