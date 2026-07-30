using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Keeps a request-global final image out of the discarded host core video pass. WAN's authored
/// session reads the original request later and applies it only to the resolved terminal stage.
/// </summary>
internal static class WanVideoEndFrameIsolation
{
    private static int _registered;

    internal static void RegisterHandlers()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }
        WorkflowGenerator.AltImageToVideoPreHandlers.Add(IgnoreCoreEndFrame);
    }

    internal static void IgnoreCoreEndFrame(
        WorkflowGenerator.ImageToVideoGenInfo genInfo)
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

        context.ExecutePrepared(() => genInfo.VideoEndFrame = null);
    }
}
