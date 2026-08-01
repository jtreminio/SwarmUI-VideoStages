using VideoStages.Architectures.Abstractions;
using VideoStages.HostVideo;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

internal sealed record WanFrameReferencePlan(
    string Source,
    string UploadFileName,
    string InlineData);

internal sealed record WanClipPayload(
    int ClipId,
    WanFrameReferencePlan FirstFrameReference = null,
    WanFrameReferencePlan LastFrameReference = null) :
    IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;

    public string CompatibilityClassId { get; init; } =
        WanArchitectureModule.ArchitectureId.Value;

    public (int Width, int Height) ProjectFinalDimensions(
        IReadOnlyList<StagePlan> stages,
        int width,
        int height) =>
        HostVideoStageGeometry.ProjectFinalDimensions(stages, width, height);
}

internal static class WanClipPlanExtensions
{
    internal static StockHostVideoStagePayload RequireWanPayload(this StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.ArchitecturePayload is not StockHostVideoStagePayload payload
            || payload.ArchitectureId != WanArchitectureModule.ArchitectureId)
        {
            throw new InvalidOperationException(
                $"Stage {stage.StageId} has no Wan architecture payload.");
        }
        return payload;
    }

    internal static WanClipPayload RequireWanPayload(this ClipPlan clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.ArchitecturePayload is not WanClipPayload payload)
        {
            throw new InvalidOperationException(
                $"Clip {clip.ClipId} has no Wan architecture payload.");
        }
        return payload;
    }
}
