using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

public static class AceStepFunAudioSavePruner
{
    private const string SaveAudioMp3 = "SaveAudioMP3";
    private const string FilenamePrefix = "SwarmUI_track_";

    public static void Apply(WorkflowGenerator g, IReadOnlyList<JsonParser.ClipSpec> clips)
    {
        if (g?.Workflow is null || clips is null || clips.Count == 0)
        {
            return;
        }

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
            if (property.Value is not JObject node
                || $"{node["class_type"]}" != SaveAudioMp3
                || !TryGetAceStepFunTrackIndex(node, out int trackIndex)
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

    private static bool TryGetAceStepFunTrackIndex(JObject node, out int trackIndex)
    {
        trackIndex = -1;
        if (node["inputs"] is not JObject inputs)
        {
            return false;
        }
        string prefix = $"{inputs["filename_prefix"] ?? ""}";
        if (!prefix.StartsWith(FilenamePrefix, StringComparison.Ordinal))
        {
            return false;
        }
        string suffix = prefix[FilenamePrefix.Length..];
        int underscore = suffix.IndexOf('_', StringComparison.Ordinal);
        if (underscore < 0 || !int.TryParse(suffix[..underscore], out int oneBasedTrack))
        {
            return false;
        }
        trackIndex = oneBasedTrack - 1;
        return trackIndex >= 0;
    }
}
