using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>
/// Host-param gating probes for the VideoStages pipeline — the questions that require inspecting
/// the live <see cref="WorkflowGenerator"/> (or mutating-and-restoring it) rather than the parsed
/// spec. Kept alongside <see cref="VideoStagesPromptSection"/>'s enable/active gate and out of the
/// parser so the parser stays a pure JSON→spec compiler.
/// </summary>
internal static class VideoStagesGate
{
    public static bool IsRefineSourceVideoMode(WorkflowGenerator g) =>
        g.UserInput.TryGet(VideoStagesExtension.RefineSourceVideo, out Image source) && source is not null;

    public static int ResolveRefineSkipStages(WorkflowGenerator g, bool refineMode)
    {
        if (!refineMode)
        {
            return 0;
        }
        return g.UserInput.TryGet(VideoStagesExtension.RefineSkipStages, out int value) ? value : 1;
    }
}
