using VideoStages.Planning;

namespace VideoStages.Timeline;

/// <summary>
/// Per-boundary trim frames for the decoded joiners, plus the total frames the trims remove from
/// the generated clips.
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
