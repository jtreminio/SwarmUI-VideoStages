using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal readonly record struct WanStaticGeneratedFrameResolution(
    int Frames,
    int FrameGrid);

/// <summary>
/// Resolves a known static Wan generated-frame request against the active model profile. Host
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
            throw new InvalidOperationException(
                $"Clip {clipId} stage {stageId} has no resolved video model.");
        }
        VideoModelProfileDescriptor profile = resolvedModel.Architecture.Profiles.SingleOrDefault(
            candidate => candidate.Id == resolvedModel.ModelProfileId)
            ?? throw new InvalidOperationException(
                $"Clip {clipId} stage {stageId} resolved undeclared model profile "
                    + $"'{resolvedModel.ModelProfileId}'.");
        return new(
            StaticGeneratedFrameGrid.SnapDown(requestedPixelFrames, profile.FrameGrid),
            profile.FrameGrid);
    }
}
