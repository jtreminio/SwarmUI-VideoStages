using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public sealed class AudioHandler(WorkflowGenerator g)
{
    private const long AceStepFunDecodeIdBase = 64160;
    private const long AceStepFunTrackIdStride = 100;
    private const long AceStepFunTrackIdWraparound = 1000;
    private const string AceStepFunAudioSourcePrefix = "audio";

    public static string MakeAceStepFunDecodeId(int trackIndex) =>
        (AceStepFunDecodeIdBase + trackIndex * AceStepFunTrackIdStride).ToString();

    public WGNodeData DetectAceStepFunAudio(string source)
    {
        if (!TryParseAceStepFunAudioSource(source, out int trackIndex))
        {
            return null;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        VAEDecodeAudioNode decode = FindAceStepFunDecode(bridge, trackIndex);
        return decode is null ? null : CreateAudioNode(decode.AUDIO);
    }

    public void PruneAceStepFunUnsavedTracks(IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        HashSet<int> usedTracks = [];
        HashSet<int> savedTracks = [];
        foreach (JsonParser.ClipSpec clip in clips)
        {
            if (!TryParseAceStepFunAudioSource(clip.AudioSource, out int trackIndex))
            {
                continue;
            }
            usedTracks.Add(trackIndex);
            if (clip.SaveAudioTrack)
            {
                savedTracks.Add(trackIndex);
            }
        }
        if (usedTracks.Count == 0)
        {
            return;
        }
        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        foreach (int trackIndex in usedTracks)
        {
            if (savedTracks.Contains(trackIndex))
            {
                continue;
            }
            VAEDecodeAudioNode decode = FindAceStepFunDecode(bridge, trackIndex);
            if (decode is null)
            {
                continue;
            }
            PruneDownstreamSaveAudio(bridge, decode);
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

    private static void PruneDownstreamSaveAudio(WorkflowBridge bridge, VAEDecodeAudioNode decode)
    {
        List<ComfyNode> toRemove = [];
        foreach (SaveAudioMP3Node save in bridge.Graph.NodesOfType<SaveAudioMP3Node>())
        {
            if (save.Audio.Connection?.Node?.Id == decode.Id)
            {
                toRemove.Add(save);
            }
        }
        foreach (SaveAudioNode save in bridge.Graph.NodesOfType<SaveAudioNode>())
        {
            if (save.Audio.Connection?.Node?.Id == decode.Id)
            {
                toRemove.Add(save);
            }
        }
        foreach (ComfyNode node in toRemove)
        {
            bridge.RemoveNode(node);
        }
    }

    private static VAEDecodeAudioNode FindAceStepFunDecode(WorkflowBridge bridge, int trackIndex)
    {
        foreach (VAEDecodeAudioNode decode in bridge.Graph.NodesOfType<VAEDecodeAudioNode>())
        {
            if (IsAceStepFunDecodeNodeForTrack(decode.Id, trackIndex))
            {
                return decode;
            }
        }
        return null;
    }

    private static bool IsAceStepFunDecodeNodeForTrack(string nodeId, int trackIndex)
    {
        if (!long.TryParse(nodeId, out long numericId))
        {
            return false;
        }
        long diff = numericId - AceStepFunDecodeIdBase;
        return diff >= 0 && diff % AceStepFunTrackIdWraparound == trackIndex * AceStepFunTrackIdStride;
    }

    private WGNodeData CreateAudioNode(INodeOutput output) =>
        new(WorkflowBridge.ToPath(output), g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
}
