using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

internal static class ArchitectureStageActivity
{
    internal static bool IsPassthrough(
        StageSpec stage,
        VideoArchitectureDescriptor descriptor) =>
        stage.Control <= 0
        && (stage.RetakeWindow is null
            || descriptor?.Features.HasFlag(ArchitectureFeature.Retake) != true)
        && !(stage.Upscale != 1
            && (stage.IsLatentUpscale || stage.IsLatentModelUpscale)
            && descriptor?.Features.HasFlag(ArchitectureFeature.LatentUpscale) == true);
}
