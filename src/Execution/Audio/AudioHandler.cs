using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

public sealed class AudioHandler(WorkflowGenerator g)
{
    private const long AceStepFunDecodeIdBase = 65160;
    private const long AceStepFunTrackIdStride = 100;
    private const string AceStepFunAudioSourcePrefix = "audio";

    public static string MakeAceStepFunDecodeId(int trackIndex) =>
        (AceStepFunDecodeIdBase + trackIndex * AceStepFunTrackIdStride).ToString();

    /// <summary>Resolves a compiled AceStepFun track identity without reparsing its legacy source text.</summary>
    public WGNodeData DetectAceStepFunAudio(int trackIndex)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        return DetectAceStepFunAudio(trackIndex, bridge);
    }

    public WGNodeData DetectAceStepFunAudio(int trackIndex, WorkflowBridge bridge)
    {
        if (trackIndex < 0)
        {
            return null;
        }
        VAEDecodeAudioNode decode = FindAceStepFunDecode(bridge, trackIndex);
        return decode is null ? null : CreateAudioNode(decode.AUDIO);
    }

    /// <summary>
    /// Prunes only compiled AceStepFun audio tracks. Save intent comes from the terminal stage output
    /// plan, so this method never needs to rediscover clip audio source strings.
    /// </summary>
    internal void PruneAceStepFunUnsavedTracks(IReadOnlyList<ClipPlan> clips)
    {
        HashSet<int> usedTracks = [];
        HashSet<int> savedTracks = [];
        foreach (ClipPlan clip in clips)
        {
            if (clip.Audio.Base.Kind != AudioSourceKind.AceStepFun
                || clip.Audio.Base.AceStepFunTrack is not int trackIndex)
            {
                continue;
            }
            usedTracks.Add(trackIndex);
            if (clip.Stages.Any(stage => stage.Output.PreserveConfiguredAudioTrackSave))
            {
                savedTracks.Add(trackIndex);
            }
        }
        PruneUnsavedTracks(usedTracks, savedTracks);
    }

    private void PruneUnsavedTracks(ISet<int> usedTracks, ISet<int> savedTracks)
    {
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
            if (decode is not null)
            {
                PruneDownstreamSaveAudio(bridge, decode);
            }
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

    private void PruneDownstreamSaveAudio(WorkflowBridge bridge, VAEDecodeAudioNode decode)
    {
        List<ComfyNode> toRemove = [];
        foreach (SaveAudioMP3Node save in bridge.Graph.NodesOfType<SaveAudioMP3Node>())
        {
            if (save.AudioInput.Connection?.Node?.Id == decode.Id)
            {
                toRemove.Add(save);
            }
        }
        foreach (SaveAudioNode save in bridge.Graph.NodesOfType<SaveAudioNode>())
        {
            if (save.AudioInput.Connection?.Node?.Id == decode.Id)
            {
                toRemove.Add(save);
            }
        }
        foreach (ComfyNode node in toRemove)
        {
            VideoGraphHelpers.RemoveNode(g, bridge, node.Id);
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
        return numericId == AceStepFunDecodeIdBase + (long)trackIndex * AceStepFunTrackIdStride;
    }

    private WGNodeData CreateAudioNode(INodeOutput output) =>
        new(WorkflowBridge.ToPath(output), g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
}
