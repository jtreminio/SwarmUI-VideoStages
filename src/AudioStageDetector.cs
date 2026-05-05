using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public sealed class AudioStageDetector(WorkflowGenerator g)
{
    private const string AceStepFunAudioSourcePrefix = "audio";
    private const long AceStepFunDecodeNodeBase = 64160;
    public const string AceStepFunFilenamePrefix = "SwarmUI_track_";
    private const int PriorityAceStepFunSave = 3;
    private const int PriorityGenericSaveAudio = 2;
    private const int PriorityDecode = 1;

    public sealed record Detection(
        WGNodeData Audio,
        string MatchedNodeId,
        string MatchedClassType,
        string SourceNodeId,
        int Priority);

    public Detection Detect()
    {
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ComfyGraph graph = bridge.Graph;
        Detection best = null;
        foreach (SaveAudioNode node in graph.NodesOfType<SaveAudioNode>())
        {
            if (IsAceStepFunSaveNode(node))
            {
                continue;
            }
            TryReplace(ref best, BuildSaveCandidate(node, PriorityGenericSaveAudio));
        }
        foreach (SaveAudioMP3Node node in graph.NodesOfType<SaveAudioMP3Node>())
        {
            if (IsAceStepFunSaveNode(node))
            {
                continue;
            }
            TryReplace(ref best, BuildSaveCandidate(node, PriorityGenericSaveAudio));
        }
        foreach (VAEDecodeAudioNode node in graph.NodesOfType<VAEDecodeAudioNode>())
        {
            if (IsAceStepFunDecodeNode(node.Id))
            {
                continue;
            }
            TryReplace(ref best, BuildDecodeCandidate(node, PriorityDecode));
        }
        return best;
    }

    public Detection DetectAceStepFunTrack(string source)
    {
        if (!TryParseAceStepFunAudioSource(source, out int trackIndex))
        {
            return null;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        ComfyGraph graph = bridge.Graph;
        Detection best = null;
        foreach (SaveAudioMP3Node node in graph.NodesOfType<SaveAudioMP3Node>())
        {
            if (!IsAceStepFunSaveNodeForTrack(node, trackIndex))
            {
                continue;
            }
            TryReplace(ref best, BuildSaveCandidate(node, PriorityAceStepFunSave));
        }
        foreach (VAEDecodeAudioNode node in graph.NodesOfType<VAEDecodeAudioNode>())
        {
            if (!IsAceStepFunDecodeNodeForTrack(node.Id, trackIndex))
            {
                continue;
            }
            TryReplace(ref best, BuildDecodeCandidate(node, PriorityDecode));
        }
        return best;
    }

    private static void TryReplace(ref Detection best, Detection candidate)
    {
        if (ShouldReplace(best, candidate))
        {
            best = candidate;
        }
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

    public static bool TryParseAceStepFunSaveNodeTrackIndex(string filenamePrefix, out int zeroBasedTrackIndex)
    {
        zeroBasedTrackIndex = -1;
        string prefix = filenamePrefix ?? "";
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

    private WGNodeData CreateAudioNode(INodeOutput output) =>
        new(WorkflowBridge.ToPath(output), g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat ?? g.CurrentCompat());

    private Detection BuildSaveCandidate(SaveAudioNode node, int priority) =>
        BuildSaveCandidateFromConnection(node, node.Audio.Connection, priority);

    private Detection BuildSaveCandidate(SaveAudioMP3Node node, int priority) =>
        BuildSaveCandidateFromConnection(node, node.Audio.Connection, priority);

    private Detection BuildSaveCandidateFromConnection(ComfyNode node, INodeOutput audioConn, int priority)
    {
        if (audioConn is null)
        {
            return null;
        }
        WGNodeData audio = CreateAudioNode(audioConn);
        return new Detection(audio, node.Id, node.ClassTypeName, audioConn.Node.Id, priority);
    }

    private Detection BuildDecodeCandidate(VAEDecodeAudioNode node, int priority)
    {
        WGNodeData audio = CreateAudioNode(node.AUDIO);
        return new Detection(audio, node.Id, node.ClassTypeName, node.Id, priority);
    }

    private static bool IsAceStepFunSaveNodeForTrack(SaveAudioMP3Node node, int trackIndex)
    {
        return TryParseAceStepFunSaveNodeTrackIndex(node.FilenamePrefix.LiteralValue as string, out int t)
            && t == trackIndex;
    }

    private static bool IsAceStepFunSaveNode(SaveAudioNode node) =>
        StartsWithAceStepFunPrefix(node.FilenamePrefix.LiteralValue as string);

    private static bool IsAceStepFunSaveNode(SaveAudioMP3Node node) =>
        StartsWithAceStepFunPrefix(node.FilenamePrefix.LiteralValue as string);

    private static bool StartsWithAceStepFunPrefix(string filenamePrefix) =>
        (filenamePrefix ?? "").StartsWith(AceStepFunFilenamePrefix, StringComparison.Ordinal);

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
