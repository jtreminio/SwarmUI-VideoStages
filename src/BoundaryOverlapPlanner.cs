using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Resolved video-frame overlap at every boundary, plus its total timeline reduction.</summary>
internal sealed record BoundaryOverlapPlan(int[] BoundaryOverlap, int RemovedFrames);

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
    internal const int DefaultCrossfadeOverlapFrames = 8;

    /// <summary>
    /// Reconciles typed boundary windows against planned clip lengths. Unknown lengths remain
    /// provisional; known short clips shrink continue windows on the LTX 8n+1 grid and cap
    /// crossfades so every clip retains at least one core frame.
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

        bool[] continues = new bool[boundaryCount];
        bool[] crossfades = new bool[boundaryCount];
        for (int i = 0; i < boundaryCount; i++)
        {
            continues[i] = source[i].Effective == BoundaryExecutionMode.Continue;
            crossfades[i] = source[i].Effective == BoundaryExecutionMode.Crossfade;
        }

        int[] continueWindows = new int[boundaryCount];
        for (int i = 0; i < boundaryCount; i++)
        {
            if (!continues[i])
            {
                continue;
            }

            int requested = Math.Max(1, source[i].ContinuityWindowFrames);
            int leftReserve = i > 0 && continues[i - 1]
                ? continueWindows[i - 1]
                : i > 0 && crossfades[i - 1] ? 1 : 0;
            int rightReserve = i < boundaryCount - 1 && (continues[i + 1] || crossfades[i + 1])
                ? 1
                : 0;
            int maximum = Math.Min(
                frames[i]!.Value - 1 - leftReserve,
                frames[i + 1]!.Value - 1 - rightReserve);
            while (requested > 1 && requested > maximum)
            {
                requested -= 8;
            }
            continueWindows[i] = Math.Max(1, requested);
        }

        int[] crossfadeMaxPerSide = new int[boundaryCount + 1];
        for (int i = 0; i <= boundaryCount; i++)
        {
            int fixedTrim = (i > 0 && continues[i - 1] ? continueWindows[i - 1] : 0)
                + (i < boundaryCount && continues[i] ? continueWindows[i] : 0);
            int crossfadeSides = (i > 0 && crossfades[i - 1] ? 1 : 0)
                + (i < boundaryCount && crossfades[i] ? 1 : 0);
            int budget = frames[i]!.Value - 1 - fixedTrim;
            if (budget < 0 || (crossfadeSides > 0 && budget / crossfadeSides < 1))
            {
                return DegradeAllToCuts(
                    source,
                    "planned clip frame counts cannot fund the requested boundary windows",
                    BoundaryFallback.InsufficientFrameBudget);
            }
            if (crossfadeSides > 0)
            {
                crossfadeMaxPerSide[i] = budget / crossfadeSides;
            }
        }

        List<BoundaryPlan> resolved = [.. source];
        bool adjusted = false;
        for (int i = 0; i < boundaryCount; i++)
        {
            BoundaryPlan boundary = source[i];
            BoundaryPlan replacement = boundary.Effective switch
            {
                BoundaryExecutionMode.Continue => boundary with
                {
                    OverlapFrames = Math.Max(0, continueWindows[i] - 1),
                    ContinuityWindowFrames = continueWindows[i],
                },
                BoundaryExecutionMode.Crossfade => boundary with
                {
                    OverlapFrames = Math.Min(
                        Math.Max(1, boundary.OverlapFrames),
                        Math.Min(crossfadeMaxPerSide[i], crossfadeMaxPerSide[i + 1])),
                    ContinuityWindowFrames = 0,
                },
                _ => boundary with
                {
                    OverlapFrames = 0,
                    ContinuityWindowFrames = 0,
                },
            };
            resolved[i] = replacement;
            adjusted |= !Equals(boundary, replacement);
        }
        return new(
            resolved.AsReadOnly(),
            Degraded: adjusted,
            adjusted ? "planned boundary windows were reduced to fit clip frame budgets" : null);
    }

    /// <summary>
    /// Validates already-resolved typed boundaries against runtime artifacts. Runtime never invents
    /// a second overlap policy: if the planned windows no longer fit, all overlaps explicitly
    /// degrade to cuts.
    /// </summary>
    internal static BoundaryBudgetResolution ValidateRuntime(
        IReadOnlyList<WGNodeData> clips,
        IReadOnlyList<BoundaryPlan> boundaries)
    {
        ArgumentNullException.ThrowIfNull(clips);
        IReadOnlyList<BoundaryPlan> source = boundaries ?? [];
        if (!source.Any(IsOverlapped))
        {
            return new(source, Degraded: false, Reason: null);
        }

        WGNodeData first = clips.FirstOrDefault();
        if (first is null
            || clips.Count != source.Count + 1
            || clips.Any(clip => !IsCompatibleRuntimeClip(clip, first)))
        {
            return DegradeAllToCuts(
                source,
                "runtime overlap needs LTX clips with known lengths and matching dimensions and fps");
        }

        BoundaryBudgetResolution runtimeBudget = ResolvePlanBudgets(
            [.. clips.Select(clip => clip.Frames)],
            source);
        if (!SameEffectiveWindows(source, runtimeBudget.Boundaries))
        {
            return DegradeAllToCuts(
                source,
                "runtime clip lengths cannot support the compiled boundary windows");
        }
        return new(source, Degraded: false, Reason: null);
    }

    internal static BoundaryOverlapPlan ToOverlapPlan(IReadOnlyList<BoundaryPlan> boundaries)
    {
        if (boundaries is null || !boundaries.Any(IsOverlapped))
        {
            return null;
        }
        int[] overlaps = [.. boundaries.Select(EffectiveTrimFrames)];
        return new(overlaps, overlaps.Sum());
    }

    internal static BoundaryBudgetResolution DegradeAllToCuts(
        IReadOnlyList<BoundaryPlan> boundaries,
        string reason,
        BoundaryFallback fallback = BoundaryFallback.None)
    {
        IReadOnlyList<BoundaryPlan> source = boundaries ?? [];
        return new(
            Array.AsReadOnly(source.Select(boundary => boundary with
            {
                Effective = BoundaryExecutionMode.Cut,
                OverlapFrames = 0,
                ContinuityWindowFrames = 0,
                RequiresRuntimeMergeValidation = false,
                Fallback = fallback == BoundaryFallback.None ? boundary.Fallback : fallback,
            }).ToArray()),
            Degraded: source.Any(IsOverlapped),
            reason);
    }

    internal static BoundaryPlan DegradeToCut(BoundaryPlan boundary) =>
        boundary with
        {
            Effective = BoundaryExecutionMode.Cut,
            OverlapFrames = 0,
            ContinuityWindowFrames = 0,
            RequiresRuntimeMergeValidation = false,
        };

    private static bool IsOverlapped(BoundaryPlan boundary) =>
        boundary?.Effective is BoundaryExecutionMode.Continue or BoundaryExecutionMode.Crossfade;

    private static int EffectiveTrimFrames(BoundaryPlan boundary) =>
        boundary.Effective switch
        {
            BoundaryExecutionMode.Continue => Math.Max(1, boundary.ContinuityWindowFrames),
            BoundaryExecutionMode.Crossfade => Math.Max(1, boundary.OverlapFrames),
            _ => 0,
        };

    private static bool IsCompatibleRuntimeClip(WGNodeData clip, WGNodeData first)
    {
        bool uniform = clip?.Width is int width && width > 0 && width == first.Width
            && clip.Height is int height && height > 0 && height == first.Height
            && SameFps(clip, first);
        return clip?.Frames is > 0
            && uniform
            && VideoStageModelCompat.IsLtxV2VideoModel(clip.Compat);
    }

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

    private static bool SameFps(WGNodeData left, WGNodeData right)
    {
        int? leftFps = left.GetRawFPS();
        int? rightFps = right.GetRawFPS();
        return leftFps is int leftValue
            && leftValue > 0
            && rightFps is int rightValue
            && rightValue > 0
            && leftValue == rightValue;
    }
}
