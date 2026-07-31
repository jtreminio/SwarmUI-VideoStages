using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Keeps request-global swap and final-frame settings out of the discarded host core pass.
/// WAN's authored session applies its own stage-local equivalents later.
/// </summary>
internal static class WanHostHandlers
{
    private static int _registered;

    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(IsolateCoreSettings);
    }

    internal static void IsolateCoreSettings(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        if (!VideoStagesContext.TryGetActiveCoreVideoContext(
                genInfo,
                out _,
                out VideoExecutionPlanContext context)
            || !context.Plan.Clips.Any(
                clip => clip.Architecture?.Id == WanArchitectureModule.ArchitectureId))
        {
            return;
        }

        context.ExecutePrepared(() =>
        {
            genInfo.VideoSwapModel = null;
            genInfo.VideoSwapPercent = 0.5;
            genInfo.VideoEndFrame = null;
        });
    }
}
