using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal readonly record struct WanStaticGeneratedFrameResolution(
    int Frames,
    int FrameGrid);

/// <summary>
/// Resolves a known static Wan generated-frame request against the active architecture. Host
/// input lookup, diagnostics, graph mutation, and artifact publication remain runtime concerns.
/// </summary>
internal static class WanStaticGeneratedFrameResolver
{
    internal static WanStaticGeneratedFrameResolution Resolve(
        int requestedPixelFrames,
        int clipId,
        int stageId,
        ResolvedVideoModel resolvedModel)
    {
        if (resolvedModel is null)
        {
            throw VideoStagesInvariant.Failure(
                $"Clip {clipId} stage {stageId} has no resolved video model.");
        }
        int frameGrid = resolvedModel.FrameGrid;
        return new(
            StaticGeneratedFrameGrid.SnapDown(requestedPixelFrames, frameGrid),
            frameGrid);
    }
}
