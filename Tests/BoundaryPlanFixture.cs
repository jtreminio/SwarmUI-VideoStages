using VideoStages.Planning;

namespace VideoStages.Tests;

/// <summary>
/// Translates authored boundary strings from shared and graph fixtures into the typed plans consumed
/// by production. String compatibility belongs to fixtures; runtime execution receives only plans.
/// </summary>
internal static class BoundaryPlanFixture
{
    internal static BoundaryBudgetResolution Resolve(
        IReadOnlyList<int?> frames,
        IReadOnlyList<string> boundaryOuts = null,
        IReadOnlyList<int> boundaryOverlapPrefs = null,
        IReadOnlyList<int> continueWindows = null)
    {
        int boundaryCount = Math.Max(0, (frames?.Count ?? 0) - 1);
        List<BoundaryPlan> boundaries = [];
        for (int i = 0; i < boundaryCount; i++)
        {
            string raw = boundaryOuts is not null && i < boundaryOuts.Count
                ? boundaryOuts[i]
                : Constants.BoundaryOutCut;
            BoundaryExecutionMode mode = BoundaryPolicy.ParsePlanMode(raw, out bool known);
            int overlap = boundaryOverlapPrefs is not null && i < boundaryOverlapPrefs.Count
                ? boundaryOverlapPrefs[i]
                : BoundaryOverlapPlanner.DefaultCrossfadeOverlapFrames;
            int continuityWindow = continueWindows is not null
                && i < continueWindows.Count
                && continueWindows[i] > 0
                    ? continueWindows[i]
                    : overlap + 1;
            boundaries.Add(new(
                i,
                mode,
                mode == BoundaryExecutionMode.Cut ? 0 : overlap,
                mode == BoundaryExecutionMode.Continue ? continuityWindow : 0,
                RequiresRuntimeMergeValidation: mode != BoundaryExecutionMode.Cut,
                known ? BoundaryFallback.None : BoundaryFallback.UnknownBoundaryKind));
        }
        return BoundaryOverlapPlanner.ResolvePlanBudgets(frames ?? [], boundaries);
    }
}
