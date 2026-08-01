using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Resolved video-frame overlap at every boundary, plus its total timeline reduction.</summary>
internal sealed record BoundaryOverlapPlan(
    int[] BoundaryOverlap,
    int RemovedFrames);

/// <summary>
/// One typed boundary-budget resolution. Planning may shrink authored windows to fit known clip
/// lengths. Runtime either validates that exact result or explicitly degrades it to cuts.
/// </summary>
internal sealed record BoundaryBudgetResolution(
    IReadOnlyList<BoundaryPlan> Boundaries,
    bool Degraded,
    string Reason);

/// <summary>Owns all frame-budget reconciliation for typed timeline boundaries.</summary>
internal static class BoundaryOverlapPlanner
{
    /// <summary>
    /// Reconciles typed boundary windows against planned clip lengths. Unknown lengths remain
    /// provisional; known short clips shrink continue windows on the architecture-projected frame
    /// grid and cap crossfades so every clip retains at least one core frame.
    /// </summary>
    internal static BoundaryBudgetResolution ResolvePlanBudgets(
        IReadOnlyList<int?> frames,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        ArgumentNullException.ThrowIfNull(frames);
        IReadOnlyList<BoundaryPlan> source = boundaries ?? [];
        int boundaryCount = Math.Min(source.Count, Math.Max(0, frames.Count - 1));
        if (boundaryCount == 0 || !source.Take(boundaryCount).Any(IsOverlapped))
        {
            return new(source, Degraded: false, Reason: null);
        }

        if (frames.Take(boundaryCount + 1).Any(frame => frame is not > 0))
        {
            return new(source, Degraded: false, Reason: null);
        }

        List<BoundaryPlan> resolved = [.. source];
        bool adjusted = false;
        while (TryFindOverBudgetClip(
            frames,
            resolved,
            boundaryCount,
            out int overBudgetClip))
        {
            int rightBoundary = overBudgetClip < boundaryCount
                && IsOverlapped(resolved[overBudgetClip])
                    ? overBudgetClip
                    : -1;
            int leftBoundary = overBudgetClip > 0
                && IsOverlapped(resolved[overBudgetClip - 1])
                    ? overBudgetClip - 1
                    : -1;
            int candidate = rightBoundary >= 0 ? rightBoundary : leftBoundary;
            if (candidate < 0)
            {
                return DegradeAllToCuts(
                    resolved,
                    "planned clip frame counts cannot fund the requested boundary windows",
                    BoundaryFallbackReason.InsufficientFrameBudget);
            }

            resolved[candidate] = ReduceOnPolicyGridOrCut(resolved[candidate]);
            adjusted = true;
        }
        return new(
            resolved.AsReadOnly(),
            Degraded: adjusted,
            adjusted
                ? "planned boundary windows were reduced on their architecture grid or degraded "
                    + "to cuts to fit clip frame budgets"
                : null);
    }

    private static bool TryFindOverBudgetClip(
        IReadOnlyList<int?> frames,
        IReadOnlyList<BoundaryPlan> boundaries,
        int boundaryCount,
        out int clipIndex)
    {
        for (int i = 0; i <= boundaryCount; i++)
        {
            int leftTrim = i > 0 ? EffectiveOverlapFrames(boundaries[i - 1]) : 0;
            int rightTrim = i < boundaryCount ? EffectiveOverlapFrames(boundaries[i]) : 0;
            if (leftTrim + rightTrim > frames[i]!.Value - 1)
            {
                clipIndex = i;
                return true;
            }
        }
        clipIndex = -1;
        return false;
    }

    private static BoundaryPlan ReduceOnPolicyGridOrCut(BoundaryPlan boundary)
    {
        int minimum = Math.Max(1, boundary.MinFrames);
        int nextOverlap = boundary.OverlapFrames - Math.Max(1, boundary.FrameStep);
        if (nextOverlap < minimum)
        {
            return DegradeToCut(boundary) with
            {
                Fallback = BoundaryFallbackReason.InsufficientFrameBudget,
            };
        }
        int continuityExtra = boundary.Effective == BoundaryJoinType.Continue
            ? Math.Max(0, boundary.ContinuityWindowFrames - boundary.OverlapFrames)
            : 0;
        return boundary with
        {
            OverlapFrames = nextOverlap,
            ContinuityWindowFrames = boundary.Effective == BoundaryJoinType.Continue
                ? nextOverlap + continuityExtra
                : 0,
        };
    }

    /// <summary>
    /// Validates already-resolved typed boundaries against runtime artifacts. Clip geometry is not
    /// re-checked here: <see cref="TimelineGeometryConform"/> has already conformed every artifact
    /// to one width, height, and fps, so only the frame budget can still fail. Each maximal
    /// overlapped run is judged on its own frames, so a cut-separated clip cannot degrade an
    /// unrelated overlap elsewhere. Runtime never invents a second overlap policy: a run whose
    /// planned windows no longer fit degrades entirely to cuts.
    /// </summary>
    internal static BoundaryBudgetResolution ValidateRuntime(
        IReadOnlyList<DecodedClipArtifact> clips,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        ArgumentNullException.ThrowIfNull(clips);
        IReadOnlyList<BoundaryPlan> source = boundaries ?? [];
        if (!source.Any(IsOverlapped))
        {
            return new(source, Degraded: false, Reason: null);
        }
        if (clips.Count != source.Count + 1)
        {
            return DegradeAllToCuts(
                source,
                "runtime overlap needs one decoded clip per planned boundary");
        }

        List<BoundaryPlan> resolved = [.. source];
        List<string> reasons = [];
        foreach ((int start, int boundaryCount) in OverlappedRuns(source))
        {
            IReadOnlyList<BoundaryPlan> runBoundaries =
                [.. source.Skip(start).Take(boundaryCount)];
            IReadOnlyList<int?> runFrames = [
                .. clips
                    .Skip(start)
                    .Take(boundaryCount + 1)
                    .Select(clip => clip?.Frames is int frames && frames > 0 ? frames : (int?)null)
            ];
            BoundaryBudgetResolution runBudget = ResolvePlanBudgets(runFrames, runBoundaries);
            if (runFrames.All(frame => frame is > 0)
                && SameEffectiveWindows(runBoundaries, runBudget.Boundaries))
            {
                continue;
            }
            for (int i = 0; i < boundaryCount; i++)
            {
                resolved[start + i] = DegradeToCut(resolved[start + i]) with
                {
                    Fallback = BoundaryFallbackReason.InsufficientFrameBudget,
                };
            }
            reasons.Add($"clips {start}-{start + boundaryCount}");
        }
        return reasons.Count == 0
            ? new(source, Degraded: false, Reason: null)
            : new(
                resolved.AsReadOnly(),
                Degraded: true,
                "runtime clip lengths cannot support the compiled boundary windows for "
                    + string.Join(", ", reasons));
    }

    /// <summary>Every maximal run of consecutive overlapped boundaries, as (start, count).</summary>
    private static IEnumerable<(int Start, int Count)> OverlappedRuns(
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        int start = -1;
        for (int i = 0; i <= boundaries.Count; i++)
        {
            bool overlapped = i < boundaries.Count && IsOverlapped(boundaries[i]);
            if (overlapped && start < 0)
            {
                start = i;
            }
            else if (!overlapped && start >= 0)
            {
                yield return (start, i - start);
                start = -1;
            }
        }
    }

    internal static BoundaryOverlapPlan ToOverlapPlan(IReadOnlyList<BoundaryPlan> boundaries)
    {
        if (boundaries is null || !boundaries.Any(IsOverlapped))
        {
            return null;
        }
        int[] overlaps = [.. boundaries.Select(EffectiveOverlapFrames)];
        return new(overlaps, overlaps.Sum());
    }

    internal static BoundaryBudgetResolution DegradeAllToCuts(
        IReadOnlyList<BoundaryPlan> boundaries,
        string reason,
        BoundaryFallbackReason fallback = BoundaryFallbackReason.None)
    {
        IReadOnlyList<BoundaryPlan> source = boundaries ?? [];
        return new(
            Array.AsReadOnly(source.Select(boundary => boundary with
            {
                Effective = BoundaryJoinType.Cut,
                OverlapFrames = 0,
                ContinuityWindowFrames = 0,
                CarryAudio = false,
                Fallback = fallback == BoundaryFallbackReason.None ? boundary.Fallback : fallback,
            }).ToArray()),
            Degraded: source.Any(IsOverlapped),
            reason);
    }

    internal static BoundaryPlan DegradeToCut(BoundaryPlan boundary) =>
        boundary with
        {
            Effective = BoundaryJoinType.Cut,
            OverlapFrames = 0,
            ContinuityWindowFrames = 0,
            CarryAudio = false,
        };

    private static bool IsOverlapped(BoundaryPlan boundary) =>
        boundary?.Effective is BoundaryJoinType.Continue or BoundaryJoinType.Crossfade;

    internal static int EffectiveOverlapFrames(BoundaryPlan boundary) =>
        boundary.Effective switch
        {
            BoundaryJoinType.Continue => Math.Max(1, boundary.ContinuityWindowFrames),
            BoundaryJoinType.Crossfade => Math.Max(1, boundary.OverlapFrames),
            _ => 0,
        };

    private static bool SameEffectiveWindows(
        IReadOnlyList<BoundaryPlan> expected,
        IReadOnlyList<BoundaryPlan> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }
        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i].Effective != actual[i].Effective
                || expected[i].OverlapFrames != actual[i].OverlapFrames
                || expected[i].ContinuityWindowFrames != actual[i].ContinuityWindowFrames)
            {
                return false;
            }
        }
        return true;
    }
}
