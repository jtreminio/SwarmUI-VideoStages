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

        Dictionary<int, bool> shouldSaveTrack = [];
        foreach (JsonParser.ClipSpec clip in clips)
        {
            if (!AudioStageDetector.TryParseAceStepFunAudioSource(clip.AudioSource, out int trackIndex))
            {
                continue;
            }
            shouldSaveTrack[trackIndex] = shouldSaveTrack.TryGetValue(trackIndex, out bool existing)
                ? existing || clip.SaveAudioTrack
                : clip.SaveAudioTrack;
        }

        if (shouldSaveTrack.Count == 0)
        {
            return;
        }

        List<string> removeNodeIds = [];
        foreach (JProperty property in g.Workflow.Properties())
        {
            if (property.Value is not JObject node
                || $"{node["class_type"]}" != SaveAudioMp3
                || !TryGetAceStepFunTrackIndex(node, out int trackIndex)
                || !shouldSaveTrack.TryGetValue(trackIndex, out bool saveTrack)
                || saveTrack)
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
