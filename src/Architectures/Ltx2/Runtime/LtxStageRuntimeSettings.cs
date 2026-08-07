using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2.Runtime;

internal static class LtxStageRuntimeSettings
{
    // These five mirror core's CompatLtxv2 arm of ImageToVideoGenInfo (WorkflowGenerator.cs ~1595),
    // which sets them inline with no constant to call. A timeline stage builds its own latent, so it
    // never runs that arm and has to restate them; change one side and change the other.
    internal const int DefaultFps = 24;
    internal const int DefaultFrameCount = 97;
    internal const double DefaultCfg = 3;
    internal const string DefaultSampler = "euler";
    internal const string DefaultScheduler = "normal";

    internal static void ApplyResolvedFpsToWorkflow(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        int fps)
    {
        if (fps <= 0)
        {
            return;
        }
        genInfo.VideoFPS = fps;
        g.UserInput.Set(T2IParamTypes.VideoFPS, fps, genInfo.ContextID);
    }

    internal static int ResolveFps(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia)
    {
        int? fps = genInfo.VideoFPS ?? sourceMedia.GetRawFPS();
        if (fps.HasValue && fps.Value > 0)
        {
            return fps.Value;
        }
        int plannedFps = g.RequireVideoExecutionPlanContext().Plan.FramesPerSecond;
        return plannedFps > 0 ? plannedFps : DefaultFps;
    }
}
