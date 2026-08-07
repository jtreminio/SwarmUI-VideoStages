using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>
/// Decides which authored stage settings actually execute. A setting the architecture does not
/// declare is dropped, and a stage left with nothing to sample is a passthrough.
/// </summary>
internal static class StagePassthroughPolicy
{
    private static bool Supports(
        VideoArchitectureDescriptor descriptor,
        ArchitectureFeature feature) =>
        descriptor?.Features.HasFlag(feature) == true;

    /// <summary>
    /// True when the stage's latent upscale runs. Every other upscale mode resizes decoded media
    /// instead, so it never brings the sampler back.
    /// </summary>
    internal static bool RunsLatentUpscale(
        StageUpscaleMode mode,
        VideoArchitectureDescriptor descriptor) =>
        mode switch
        {
            StageUpscaleMode.Latent =>
                Supports(descriptor, ArchitectureFeature.LatentUpscale),
            StageUpscaleMode.LatentModel =>
                Supports(descriptor, ArchitectureFeature.LatentModelUpscale),
            _ => false,
        };

    internal static bool IsPassthrough(
        StageSpec stage,
        VideoArchitectureDescriptor descriptor) =>
        stage.Control <= 0
        && !(stage.RetakeWindow is not null
            && Supports(descriptor, ArchitectureFeature.Retake))
        && !RunsLatentUpscale(StageUpscalePlanCompiler.Mode(stage), descriptor);
}
