using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Tests;

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
            BoundaryJoinType mode = BoundaryPlanCompiler.ParseJoinType(raw);
            int overlap = boundaryOverlapPrefs is not null && i < boundaryOverlapPrefs.Count
                ? boundaryOverlapPrefs[i]
                : Ltx2BoundaryPolicy.DefaultFrames;
            int continuityWindow = continueWindows is not null
                && i < continueWindows.Count
                && continueWindows[i] > 0
                    ? continueWindows[i]
                    : overlap + 1;
            boundaries.Add(new(
                i,
                mode,
                mode == BoundaryJoinType.Cut ? 0 : overlap,
                mode == BoundaryJoinType.Continue ? continuityWindow : 0,
                BoundaryFallbackReason.None)
            {
                FrameStep = 8,
                MinFrames = mode == BoundaryJoinType.Cut ? 0 : 8,
            });
        }
        return BoundaryOverlapPlanner.FitPlanToFrameBudgets(frames ?? [], boundaries);
    }
}
