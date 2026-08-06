using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution.Audio;
using VideoStages.Planning;

namespace VideoStages.Execution;

/// <summary>
/// Mixes a clip's timeline audio tracks over its decoded audio, for the architectures that do not
/// consume those tracks themselves. The overlay lands after generation, so it plays over any video
/// model: a model with native audio keeps that audio as the bed, and one without gets a silent bed
/// of the clip's own length.
/// </summary>
internal static class DecodedTimelineAudioOverlay
{
    internal static DecodedClipArtifact Apply(
        WorkflowGenerator g,
        DecodedClipArtifact artifact,
        ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Architecture?.ConsumesTimelineAudio != false
            || clip.Audio.Segments.Items.IsDefaultOrEmpty)
        {
            return artifact;
        }
        WGNodeData baseAudio = artifact.Audio is null
            ? null
            : new WGNodeData(artifact.Audio.ToPath(), g, WGNodeData.DT_AUDIO, null);
        WGNodeData combined = new AudioSegmentCombiner(g).Combine(
            clip.ClipId,
            clip.Audio.Segments,
            baseAudio,
            ClipAudioBedDuration.Seconds(clip, artifact.FramesPerSecond, null),
            out _);
        return combined?.Path is JArray path
            ? artifact with { Audio = DecodedOutputHandle.FromPath(path) }
            : artifact;
    }
}
