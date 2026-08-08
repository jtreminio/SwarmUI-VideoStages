using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Runtime.Stage;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

internal sealed record Ltx2ClipPayload(
    Ltx2AudioInjectionPlan AudioInjection,
    int? ControlNetSourceIndex,
    ReferenceFramingMode ReferenceFraming) :
    IArchitectureClipPayload,
    IArchitectureControlNetSourcePlan
{
    public ArchitectureId ArchitectureId => Ltx2ArchitectureModule.ArchitectureId;

    /// <summary>
    /// Replays the runtime upscale rules over the authored stage chain: latent upscales apply in
    /// latent space and, once one has run, later pixel/model requests are ignored.
    /// </summary>
    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height)
    {
        bool hasLatentUpscale = false;
        foreach (StagePlan stage in stages ?? [])
        {
            StageUpscalePlan upscale = stage.Core.Upscale;
            bool isLatent = upscale.Mode is StageUpscaleMode.Latent or StageUpscaleMode.LatentModel;
            if (!isLatent
                && (upscale.Mode is not (StageUpscaleMode.Pixel or StageUpscaleMode.Model)
                    || hasLatentUpscale
                    || string.IsNullOrWhiteSpace(upscale.RawMethod)))
            {
                continue;
            }
            (width, height) = StageDimensionRules.ResolveUpscaled(stage, width, height);
            hasLatentUpscale |= isLatent;
        }
        return (width, height);
    }
}
