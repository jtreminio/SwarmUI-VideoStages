using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public static class AceStepFunAudioSavePruner
{
    public static void Apply(WorkflowGenerator g, IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        HashSet<int> tracksToSave = [];
        HashSet<int> aceSourceTracks = [];
        foreach (JsonParser.ClipSpec clip in clips)
        {
            if (!AudioStageDetector.TryParseAceStepFunAudioSource(clip.AudioSource, out int trackIndex))
            {
                continue;
            }
            aceSourceTracks.Add(trackIndex);
            if (clip.SaveAudioTrack)
            {
                tracksToSave.Add(trackIndex);
            }
        }
        if (aceSourceTracks.Count == 0)
        {
            return;
        }

        WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        List<SaveAudioMP3Node> toRemove = [];
        foreach (SaveAudioMP3Node node in bridge.Graph.NodesOfType<SaveAudioMP3Node>())
        {
            if (bridge.Workflow[node.Id] is not JObject jsonNode
                || !AudioStageDetector.TryParseAceStepFunSaveNodeTrackIndex(jsonNode, out int trackIndex)
                || !aceSourceTracks.Contains(trackIndex)
                || tracksToSave.Contains(trackIndex))
            {
                continue;
            }
            toRemove.Add(node);
        }

        foreach (SaveAudioMP3Node node in toRemove)
        {
            bridge.RemoveNode(node);
        }
    }
}
