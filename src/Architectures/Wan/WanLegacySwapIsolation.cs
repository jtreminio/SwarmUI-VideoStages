using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Keeps SwarmUI's legacy request-global swap pass out of the discarded core image-to-video pass
/// for active WAN VideoStages workflows, without changing the authored request retained in
/// metadata or intercepting architecture-authored/custom passes.
/// </summary>
internal static class WanLegacySwapIsolation
{
    private static int _registered;

    internal static void RegisterHandlers()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(IgnoreLegacySwap);
    }

    internal static void IgnoreLegacySwap(WorkflowGenerator.ImageToVideoGenInfo genInfo)
    {
        WorkflowGenerator generator = genInfo?.Generator;
        if (generator is null
            || genInfo.ContextID != T2IParamInput.SectionID_Video
            || !VideoStagesPromptSection.IsActive(generator))
        {
            return;
        }

        VideoExecutionPlanContext context = generator.GetVideoExecutionPlanContext();
        if (context is null)
        {
            return;
        }
        if (context.Plan.Clips.Any(
                clip => clip.Architecture?.Id == WanArchitectureModule.ArchitectureId)
            != true)
        {
            return;
        }

        context.ExecutePrepared(() =>
        {
            genInfo.VideoSwapModel = null;
            genInfo.VideoSwapPercent = 0.5;
        });
    }
}
