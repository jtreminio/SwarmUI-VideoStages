using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public sealed class AudioStageDetector(WorkflowGenerator g)
{
    private const string AceStepFunAudioSourcePrefix = "audio";
    private const long AceStepFunDecodeNodeBase = 64160;
    public const string AceStepFunSaveNodeType = "SaveAudioMP3";
    public const string AceStepFunFilenamePrefix = "SwarmUI_track_";

    private const int PrioritySwarmSaveWsOrAceStepFunSave = 3;
    private const int PriorityGenericSaveAudio = 2;
    private const int PriorityDecode = 1;

    public sealed record Detection(
        WGNodeData Audio,
        string MatchedNodeId,
        string MatchedClassType,
        string SourceNodeId,
        int Priority);

    public Detection? Detect()
    {
        return ScanWorkflow((nodeId, node) =>
        {
            string classType = ClassTypeOf(node);
            return classType switch
            {
                NodeTypes.SwarmSaveAudioWS => BuildSaveCandidate(nodeId, classType, node, PrioritySwarmSaveWsOrAceStepFunSave),
                _ when IsSaveAudioNode(classType) && !IsAceStepFunSaveNode(node) =>
                    BuildSaveCandidate(nodeId, classType, node, PriorityGenericSaveAudio),
                NodeTypes.VAEDecodeAudio when !IsAceStepFunDecodeNode(nodeId) =>
                    BuildDecodeCandidate(nodeId, classType, PriorityDecode),
                _ => null,
            };
        });
    }

    public Detection? DetectAceStepFunTrack(string source)
    {
        if (!TryParseAceStepFunAudioSource(source, out int trackIndex))
        {
            return null;
        }
        return ScanWorkflow((nodeId, node) =>
        {
            string classType = ClassTypeOf(node);
            return classType switch
            {
                AceStepFunSaveNodeType when IsAceStepFunSaveNodeForTrack(node, trackIndex) =>
                    BuildSaveCandidate(nodeId, classType, node, PrioritySwarmSaveWsOrAceStepFunSave),
                NodeTypes.VAEDecodeAudio when IsAceStepFunDecodeNodeForTrack(nodeId, trackIndex) =>
                    BuildDecodeCandidate(nodeId, classType, PriorityDecode),
                _ => null,
            };
        });
    }

    private Detection? ScanWorkflow(Func<string, JObject, Detection?> classifyNode)
    {
        Detection? best = null;
        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node)
            {
                continue;
            }
            Detection? candidate = classifyNode(property.Name, node);
            if (ShouldReplace(best, candidate))
            {
                best = candidate;
            }
        }
        return best;
    }

    public static bool TryParseAceStepFunAudioSource(string source, out int trackIndex)
    {
        trackIndex = -1;
        string trimmed = (source ?? "").Trim();
        if (!trimmed.StartsWith(AceStepFunAudioSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string indexText = trimmed[AceStepFunAudioSourcePrefix.Length..];
        return int.TryParse(indexText, out trackIndex) && trackIndex >= 0;
    }

    public static bool TryParseAceStepFunSaveNodeTrackIndex(JObject node, out int zeroBasedTrackIndex)
    {
        zeroBasedTrackIndex = -1;
        if (node["inputs"] is not JObject inputs)
        {
            return false;
        }
        string prefix = inputs["filename_prefix"]?.Value<string>() ?? "";
        if (!prefix.StartsWith(AceStepFunFilenamePrefix, StringComparison.Ordinal))
        {
            return false;
        }
        string suffix = prefix[AceStepFunFilenamePrefix.Length..];
        int underscore = suffix.IndexOf('_', StringComparison.Ordinal);
        if (underscore < 0 || !int.TryParse(suffix[..underscore], out int oneBasedTrack))
        {
            return false;
        }
        zeroBasedTrackIndex = oneBasedTrack - 1;
        return zeroBasedTrackIndex >= 0;
    }

    internal static string ClassTypeOf(JObject node) => node["class_type"]?.Value<string>() ?? "";

    private WGNodeData CreateAudioNode(JArray path) =>
        new(path, g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat ?? g.CurrentCompat());

    private Detection? BuildSaveCandidate(
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

    private Detection? BuildDecodeCandidate(
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

    private static bool IsAceStepFunSaveNodeForTrack(JObject node, int trackIndex)
    {
        return TryParseAceStepFunSaveNodeTrackIndex(node, out int t) && t == trackIndex;
    }

    private static bool IsAceStepFunSaveNode(JObject node)
    {
        if (node["inputs"] is not JObject inputs)
        {
            return false;
        }
        string prefix = $"{inputs["filename_prefix"] ?? ""}";
        return prefix.StartsWith(AceStepFunFilenamePrefix, StringComparison.Ordinal);
    }

    private static bool TryAceStepFunDecodeDiff(string nodeId, out long diffFromBase)
    {
        if (!long.TryParse(nodeId, out long numericId))
        {
            diffFromBase = 0;
            return false;
        }
        diffFromBase = numericId - AceStepFunDecodeNodeBase;
        return diffFromBase >= 0;
    }

    private static bool IsAceStepFunDecodeNode(string nodeId)
    {
        return TryAceStepFunDecodeDiff(nodeId, out long diff) && diff % 100 == 0;
    }

    private static bool IsAceStepFunDecodeNodeForTrack(string nodeId, int trackIndex)
    {
        return TryAceStepFunDecodeDiff(nodeId, out long diff) && diff % 1000 == trackIndex * 100L;
    }

    private static bool ShouldReplace(Detection? current, Detection? candidate)
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
