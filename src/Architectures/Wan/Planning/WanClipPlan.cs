using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

/// <summary>
/// The complete graph-free instruction for one Wan stage. The common plan treats this as opaque.
/// <c>Control</c> is regeneration strength, so a lower value starts sampling later in the schedule.
/// </summary>
internal sealed record WanStagePayload(
    string Model,
    double Control,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler) : IArchitectureStagePayload
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;
}

internal sealed record WanClipPayload(
    int ClipId) :
    IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId => WanArchitectureModule.ArchitectureId;
}

internal static class WanClipPlanExtensions
{
    internal static WanStagePayload RequireWanPayload(this StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.ArchitecturePayload is not WanStagePayload payload)
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
