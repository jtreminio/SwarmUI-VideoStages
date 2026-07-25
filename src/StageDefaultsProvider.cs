using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

/// <summary>
/// Reads the Swarm host params that seed per-stage defaults (video steps / CFG with their
/// text-to-image fallbacks, sampler, scheduler). Kept out of <see cref="VideoStagesSpecParser"/>
/// so the parser stays a pure JSON→spec compiler and never touches host params directly.
/// </summary>
internal static class StageDefaultsProvider
{
    public static (int Steps, double CfgScale, string Sampler, string Scheduler) ReadHostDefaults(WorkflowGenerator g)
    {
        int steps = g.UserInput.TryGet(T2IParamTypes.VideoSteps, out int explicitVideoSteps)
            ? explicitVideoSteps
            : g.UserInput.Get(T2IParamTypes.Steps, 8, autoFixDefault: true);
        double cfgScale = g.UserInput.TryGet(T2IParamTypes.VideoCFG, out double explicitVideoCfg)
            ? explicitVideoCfg
            : g.UserInput.Get(T2IParamTypes.CFGScale, 1, autoFixDefault: true);
        string sampler = ComfyUIBackendExtension.SamplerParam is null
            ? "euler"
            : g.UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler", autoFixDefault: true);
        string scheduler = ComfyUIBackendExtension.SchedulerParam is null
            ? "normal"
            : g.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal", autoFixDefault: true);

        return (Math.Max(1, steps), cfgScale, sampler, scheduler);
    }
}
