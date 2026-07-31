using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// The single rule for the silent-bed length a clip's audio segments are placed against. Both the
/// staged (<c>ClipAudioPreparer</c>) and init-video-only (<c>SourceOnlyClipAudioPreparer</c>) paths
/// feed <see cref="AudioSegmentCombiner"/>, so a resampled initVideoClip clip must not shift its segments
/// purely because it happens to carry stages.
/// </summary>
internal static class ClipAudioBedDuration
{
    /// <summary>Timeline fps wins; installed media fps is the fallback when the plan has none.</summary>
    internal static double Seconds(
        ClipPlan clip,
        int plannedFramesPerSecond,
        WGNodeData media)
    {
        int? fps = plannedFramesPerSecond > 0
            ? plannedFramesPerSecond
            : media?.GetRawFPS();
        return clip?.Frames is int frames && fps is > 0
            ? (double)frames / fps.Value
            : 0;
    }
}
