using VideoStages.Planning;

namespace VideoStages.Execution.StockHost;

internal static class HostVideoStageGeometry
{
    internal static (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscalePlan upscale = stage.Core.Upscale;
            if (upscale is null
                || upscale.Mode is not (StageUpscaleMode.Pixel or StageUpscaleMode.Model)
                || stage.Input is not (
                    StageInputKind.InitVideo
                    or StageInputKind.PreviousStage))
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
