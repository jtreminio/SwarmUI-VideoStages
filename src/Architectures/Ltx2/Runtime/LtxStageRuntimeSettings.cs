using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxStageRuntimeSettings(WorkflowGenerator g)
{
    internal const int DefaultFps = 24;
    internal const int DefaultFrameCount = 97;
    internal const double DefaultCfg = 3;
    internal const string DefaultSampler = "euler";
    internal const string DefaultScheduler = "normal";

    internal void ApplyResolvedFpsToWorkflow(
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

    internal int ResolveFps(
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
