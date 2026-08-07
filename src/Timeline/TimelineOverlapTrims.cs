using VideoStages.Planning;

namespace VideoStages.Timeline;

/// <summary>
/// The overlap geometry the decoded joiners read back: per-boundary frames each side trims, how many
/// of those are pre-roll the incoming clip generated, and the net frames the trims remove from the
/// timeline.
/// </summary>
internal sealed record TimelineOverlapTrims(
    int[] TrimFrames,
    int[] IncomingHandleFrames,
    int RemovedFrames)
{
    internal static TimelineOverlapTrims From(IReadOnlyList<BoundaryPlan> boundaries)
    {
        if (boundaries is null || !boundaries.Any(BoundaryOverlaps.IsOverlapped))
        {
            return null;
        }
        int[] trims = [.. boundaries.Select(BoundaryOverlaps.EffectiveOverlapFrames)];
        int[] handles = [.. boundaries.Select(BoundaryOverlaps.IncomingHandleFrames)];
        return new(trims, handles, trims.Sum());
    }
}
