using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public static class AceStepFunAudioSavePruner
{
    public static void Apply(WorkflowGenerator g, IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        HashSet<int> tracksToSave = [];
        HashSet<int> selectedAceStepFunTracks = [];
        foreach (JsonParser.ClipSpec clip in clips)
        {
            if (!AudioStageDetector.TryParseAceStepFunAudioSource(clip.AudioSource, out int trackIndex))
            {
                continue;
            }
            selectedAceStepFunTracks.Add(trackIndex);
            if (clip.SaveAudioTrack)
            {
                tracksToSave.Add(trackIndex);
            }
        }
        if (selectedAceStepFunTracks.Count == 0)
        {
            return;
        }

        List<string> removeNodeIds = [];
        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node)
            {
                continue;
            }
            string classType = AudioStageDetector.ClassTypeOf(node);
            if (!string.Equals(classType, AudioStageDetector.AceStepFunSaveNodeType, StringComparison.Ordinal))
            {
                continue;
            }
            if (!AudioStageDetector.TryParseAceStepFunSaveNodeTrackIndex(node, out int trackIndex)
                || !selectedAceStepFunTracks.Contains(trackIndex)
                || tracksToSave.Contains(trackIndex))
            {
                continue;
            }
            removeNodeIds.Add(property.Name);
        }

        foreach (string nodeId in removeNodeIds)
        {
            g.Workflow.Remove(nodeId);
        }
    }
}
