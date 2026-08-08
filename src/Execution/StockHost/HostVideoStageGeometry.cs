using VideoStages.Planning;

namespace VideoStages.Execution.StockHost;

internal static class HostVideoStageGeometry
{
    /// <summary>The modes <see cref="VideoStageRunner"/> resizes for; the rest are latent-side or
    /// unsupported and never reach the pixel resize.</summary>
    internal static bool ResizesDecodedPixels(StageUpscalePlan upscale) =>
        upscale is { Mode: StageUpscaleMode.Pixel or StageUpscaleMode.Model };

    internal static (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscalePlan upscale = stage.Core.Upscale;
            // The runtime resizes whatever decoded media is live, which every input kind but a text
            // latent guarantees; that one only has media when an architecture materialized a first
            // frame, and the plan cannot know whether one exists.
            if (!ResizesDecodedPixels(upscale)
                || stage.Input == StageInputKind.EmptyLatent)
            {
                continue;
            }
            (width, height) = StageUpscaleGraph.ResolveTargetDimensions(
                width,
                height,
                upscale.Factor);
        }
        return (width, height);
    }
}
