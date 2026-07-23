using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

/// <summary>Normalizes authored boundaries and reports deterministic continuity fallbacks.</summary>
internal static class BoundaryPlanCompiler
{
    internal static BoundaryPlanningResult Compile(
        IReadOnlyList<ClipSpec> clips,
        IReadOnlyList<ClipPlan> plannedClips = null)
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
            ClipPlan plannedFrom = plannedClips is not null && i < plannedClips.Count
                ? plannedClips[i]
                : null;
            ClipPlan plannedTo = plannedClips is not null && i + 1 < plannedClips.Count
                ? plannedClips[i + 1]
                : null;
            IArchitectureBoundaryPolicy boundaryPolicy =
                (plannedFrom?.ArchitecturePayload as IArchitectureBoundaryPolicySource)
                    ?.BoundaryPolicy;
            ArchitectureBoundaryModePolicy modePolicy =
                boundaryPolicy?.Modes.GetValueOrDefault(requested);
            if (!isKnown)
            {
                effective = BoundaryExecutionMode.Cut;
            }
            else if (requested != BoundaryExecutionMode.Cut
                && plannedFrom?.Architecture is not null
                && plannedTo?.Architecture is not null
                && plannedFrom.Architecture.Id != plannedTo.Architecture.Id)
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.ArchitectureRuleUnsupported;
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Error,
                    "boundary-cross-architecture-non-cut",
                    $"Clip {from.Id} boundary '{from.BoundaryOut}' is invalid between "
                        + $"architecture '{plannedFrom.Architecture.Id}' and "
                        + $"'{plannedTo.Architecture.Id}'. Cross-architecture boundaries must be cuts.",
                    from.Id));
            }
            else if (requested != BoundaryExecutionMode.Cut
                && plannedFrom?.Architecture?.BoundaryRules?.GetValueOrDefault(requested)
                    is RuleDecision { Support: RuleSupport.Unsupported } rule)
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = BoundaryFallback.ArchitectureRuleUnsupported;
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Error,
                    rule.Code,
                    rule.Reason,
                    from.Id));
            }
            else if (requested != BoundaryExecutionMode.Cut
                && EvaluateTarget(modePolicy, to) is { } targetFallback)
            {
                effective = BoundaryExecutionMode.Cut;
                fallback = targetFallback;
            }

            int overlap = effective == BoundaryExecutionMode.Cut
                ? 0
                : modePolicy?.NormalizeOverlap(from.BoundaryOutOverlap)
                    ?? Math.Max(1, from.BoundaryOutOverlap);
            int continuityWindow = effective == BoundaryExecutionMode.Continue
                ? overlap + (modePolicy?.ContinuityExtraFrames ?? 0)
                : 0;
            bool targetHasGenerationStage = plannedTo?.Stages is { Count: > 0 }
                || (plannedTo is null && to.Stages is { Count: > 0 });
            if (fallback != BoundaryFallback.None)
            {
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Warning,
                    $"boundary-{fallback.ToString().ToLowerInvariant()}",
                    $"Clip {from.Id} boundary '{from.BoundaryOut}' falls back to a cut: {DescribeFallback(fallback)}",
                    from.Id));
            }
            if (from.BoundaryOutCarryAudio
                && effective != BoundaryExecutionMode.Cut
                && !targetHasGenerationStage)
            {
                diagnostics.Add(new VideoPlanDiagnostic(
                    VideoPlanDiagnosticSeverity.Warning,
                    "boundary-audio-carry-target-has-no-stage",
                    $"Clip {from.Id} cannot carry audio across its '{from.BoundaryOut}' boundary "
                        + "because the next clip has no generation stage to consume it.",
                    from.Id));
            }
            boundaries.Add(new BoundaryPlan(
                from.Id,
                requested,
                effective,
                overlap,
                continuityWindow,
                RequiresRuntimeMergeValidation: effective != BoundaryExecutionMode.Cut,
                fallback)
            {
                FrameStep = modePolicy?.FrameStep ?? 1,
                MinFrames = effective == BoundaryExecutionMode.Cut
                    ? 0
                    : modePolicy?.MinFrames ?? 1,
                CarryAudio = effective != BoundaryExecutionMode.Cut
                    && from.BoundaryOutCarryAudio
                    && targetHasGenerationStage,
            });
        }
        return new BoundaryPlanningResult(boundaries.ToImmutable(), diagnostics.ToImmutable());
    }

    private static BoundaryFallback? EvaluateTarget(
        ArchitectureBoundaryModePolicy policy,
        ClipSpec target)
    {
        if (policy is null)
        {
            return null;
        }
        if (policy.TargetRequiresGeneratedEntry && target.SourceVideo is not null)
        {
            return BoundaryFallback.TargetIsSourcedVideo;
        }
        if (policy.TargetRequiresStage && target.Stages is not { Count: > 0 })
        {
            return BoundaryFallback.TargetHasNoStage;
        }
        if (policy.TargetDisallowsInitialReference && HasExplicitFirstFrameReference(target))
        {
            return BoundaryFallback.TargetHasFirstFrameReference;
        }
        return null;
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
        BoundaryFallback.ArchitectureRuleUnsupported => "the clip architecture does not support the requested join",
        _ => "the boundary is not applicable",
    };
}

internal sealed record BoundaryPlanningResult(
    ImmutableArray<BoundaryPlan> Boundaries,
    ImmutableArray<VideoPlanDiagnostic> Diagnostics);
