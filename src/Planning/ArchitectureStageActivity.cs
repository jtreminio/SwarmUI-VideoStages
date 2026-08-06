using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;

namespace VideoStages.Planning;

internal static class ArchitectureStageActivity
{
    internal static bool IsPassthrough(
        StageSpec stage,
        VideoArchitectureDescriptor descriptor)
    {
        StageUpscaleMode upscale = StageUpscalePlanCompiler.Classify(stage.UpscaleMethod);
        return stage.Control <= 0
            && (stage.RetakeWindow is null
                || descriptor?.Features.HasFlag(ArchitectureFeature.Retake) != true)
            && !(stage.Upscale != 1
                && ((upscale == StageUpscaleMode.Latent
                        && descriptor?.Features.HasFlag(ArchitectureFeature.LatentUpscale) == true)
                    || (upscale == StageUpscaleMode.LatentModel
                        && descriptor?.Features.HasFlag(
                            ArchitectureFeature.LatentModelUpscale) == true)));
    }
}
