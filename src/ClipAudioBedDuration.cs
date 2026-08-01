using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>
/// Computes the silent-bed duration used to place a clip's audio segments.
/// </summary>
internal static class ClipAudioBedDuration
{
    /// <summary>Uses planned fps when available, otherwise the installed media fps.</summary>
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
