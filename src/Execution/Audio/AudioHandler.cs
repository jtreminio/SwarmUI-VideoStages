using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Graph;
using VideoStages.Planning;

namespace VideoStages.Execution.Audio;

public sealed class AudioHandler(WorkflowGenerator g)
{
    private const long AceStepFunDecodeIdBase = 65160;
    private const long AceStepFunTrackIdStride = 100;

    public static string MakeAceStepFunDecodeId(int trackIndex) =>
        (AceStepFunDecodeIdBase + trackIndex * AceStepFunTrackIdStride).ToString();

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
        VAEDecodeAudioNode decode = bridge.Graph.GetNode<VAEDecodeAudioNode>(
            MakeAceStepFunDecodeId(trackIndex));
        return decode is null ? null : CreateAudioNode(decode.AUDIO);
    }

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
            if (clip.SavesAudioTrack)
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
            VAEDecodeAudioNode decode = bridge.Graph.GetNode<VAEDecodeAudioNode>(
                MakeAceStepFunDecodeId(trackIndex));
            if (decode is not null)
            {
                PruneDownstreamSaveAudio(bridge, decode);
            }
        }
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

    private WGNodeData CreateAudioNode(INodeOutput output) =>
        new(WorkflowBridge.ToPath(output), g, WGNodeData.DT_AUDIO, g.CurrentAudioVae?.Compat ?? g.CurrentCompat());
}
