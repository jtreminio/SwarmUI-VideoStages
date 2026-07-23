using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Normalizes authored boundaries and reports deterministic continuity fallbacks.</summary>
internal static class BoundaryPlanCompiler
{
    internal static BoundaryPlanningResult Compile(IReadOnlyList<ClipSpec> clips)
    {
        ImmutableArray<BoundaryPlan>.Builder boundaries = ImmutableArray.CreateBuilder<BoundaryPlan>();
        ImmutableArray<VideoPlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<VideoPlanDiagnostic>();
        for (int i = 0; i < clips.Count - 1; i++)
        {
            ClipSpec from = clips[i];
            BoundaryExecutionMode requested = BoundaryPolicy.ParsePlanMode(from.BoundaryOut, out bool isKnown);
            ClipSpec to = clips[i + 1];
            BoundaryFallback fallback = isKnown ? BoundaryFallback.None : BoundaryFallback.UnknownBoundaryKind;
            BoundaryExecutionMode effective = requested;
            if (!isKnown)
            {
                effective = BoundaryExecutionMode.Cut;
            }
            else if (requested == BoundaryExecutionMode.Continue && to.SourceVideo is not null)
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.TargetIsSourcedVideo;
            }
            else if (requested == BoundaryExecutionMode.Continue && to.Stages is not { Count: > 0 })
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.TargetHasNoStage;
            }
            else if (requested == BoundaryExecutionMode.Continue && HasExplicitFirstFrameReference(to))
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.TargetHasFirstFrameReference;
            }

            int overlap = effective == BoundaryExecutionMode.Cut ? 0 : BoundaryPolicy.NormalizeOverlap(from.BoundaryOutOverlap);
            int continuityWindow = effective == BoundaryExecutionMode.Continue ? overlap + 1 : 0;
            if (fallback != BoundaryFallback.None)
            {
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Warning,
                    $"boundary-{fallback.ToString().ToLowerInvariant()}",
                    $"Clip {from.Id} boundary '{from.BoundaryOut}' falls back to a cut: {DescribeFallback(fallback)}",
                    from.Id));
            }
            boundaries.Add(new BoundaryPlan(
                from.Id,
                effective,
                overlap,
                continuityWindow,
                RequiresRuntimeMergeValidation: effective != BoundaryExecutionMode.Cut,
                fallback));
        }
        return new BoundaryPlanningResult(boundaries.ToImmutable(), diagnostics.ToImmutable());
    }

    private static bool HasExplicitFirstFrameReference(ClipSpec clip) =>
        clip.ImageRefs?.Any(reference => !reference.FromEnd && reference.Frame == 1) == true;

    private static string DescribeFallback(BoundaryFallback fallback) => fallback switch
    {
        BoundaryFallback.TargetIsSourcedVideo => "the next clip is sourced footage",
        BoundaryFallback.TargetHasNoStage => "the next clip has no stage that can consume continuity",
        BoundaryFallback.TargetHasFirstFrameReference => "the next clip has an explicit first-frame reference",
        BoundaryFallback.UnknownBoundaryKind => "the requested boundary mode is unknown",
        BoundaryFallback.InsufficientFrameBudget => "the adjacent clips are too short for the requested overlap",
        _ => "the boundary is not applicable",
    };
}

internal sealed record BoundaryPlanningResult(
    ImmutableArray<BoundaryPlan> Boundaries,
    ImmutableArray<VideoPlanDiagnostic> Diagnostics);
