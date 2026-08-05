using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal sealed record WanClipPayload(
    NativeFrameReferencePlan FirstFrameReference = null,
    NativeFrameReferencePlan LastFrameReference = null) :
    IArchitectureClipPayload, INativeFrameReferenceClipPayload
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height) =>
        HostVideoStageGeometry.ProjectFinalDimensions(stages, width, height);
}

internal static class WanClipPlanExtensions
{
    internal static WanClipPayload RequireWanPayload(this ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.ArchitecturePayload is not WanClipPayload payload)
        {
            throw Invariant.Failure(
                $"Clip {clip.ClipId} has no Wan architecture payload.");
        }
        return payload;
    }
}
