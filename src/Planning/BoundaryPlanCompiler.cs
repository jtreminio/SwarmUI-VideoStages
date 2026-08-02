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
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        for (int i = 0; i < clips.Count - 1; i++)
        {
            ClipSpec from = clips[i];
            BoundaryJoinType effectiveRequested =
                BoundaryPolicy.ParsePlanMode(from.BoundaryOut);
            ClipSpec to = clips[i + 1];
            BoundaryFallbackReason fallback = BoundaryFallbackReason.None;
            bool fallbackReported = false;
            BoundaryJoinType effective = effectiveRequested;
            ClipPlan plannedFrom = plannedClips is not null && i < plannedClips.Count
                ? plannedClips[i]
                : null;
            ClipPlan plannedTo = plannedClips is not null && i + 1 < plannedClips.Count
                ? plannedClips[i + 1]
                : null;
            bool targetHasGenerationStage = plannedTo?.Stages.Any(stage => !stage.IsPassthrough)
                ?? to.Stages is { Count: > 0 };
            bool targetHasDerivedDuration = plannedTo is not null
                ? plannedTo.Audio.Length.Owner != AudioLengthOwner.Timeline
                : to.ClipLengthFromControlNet || AudioSourceKindPolicy.AudioOwnsClipDuration(to);
            // Compile the same typed rule advertised by the architecture catalog.
            RuleDecision modePolicy = plannedFrom?.Architecture?.BoundaryPolicy
                ?.Rules.GetValueOrDefault(effectiveRequested);
            BoundaryRuleConstraints constraints = modePolicy?.Constraints;
            if (effectiveRequested != BoundaryJoinType.Cut
                && plannedFrom?.Architecture is not null
                && plannedTo?.Architecture is not null
                && plannedFrom.Architecture.Id != plannedTo.Architecture.Id)
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.ArchitectureRuleUnsupported;
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "boundary-cross-architecture-non-cut",
                    $"Clip {from.Id} boundary '{from.BoundaryOut}' is invalid between "
                        + $"architecture '{plannedFrom.Architecture.Id}' and "
                        + $"'{plannedTo.Architecture.Id}'. Cross-architecture boundaries must be cuts.",
                    from.Id));
                fallbackReported = true;
            }
            else if (effectiveRequested != BoundaryJoinType.Cut
                && modePolicy is { Support: RuleSupport.Unsupported })
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.ArchitectureRuleUnsupported;
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    modePolicy.Code,
                    modePolicy.Reason,
                    from.Id));
                fallbackReported = true;
            }
            else if (effectiveRequested != BoundaryJoinType.Cut
                && EvaluateTarget(constraints, to) is { } targetFallback)
            {
                effective = BoundaryJoinType.Cut;
                fallback = targetFallback;
            }
            else if (effectiveRequested == BoundaryJoinType.Continue
                && !targetHasGenerationStage)
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.TargetHasNoStage;
            }
            else if (effectiveRequested == BoundaryJoinType.Continue
                && targetHasDerivedDuration)
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.TargetHasDerivedDuration;
            }

            int overlap = effective == BoundaryJoinType.Cut
                ? 0
                : modePolicy is null
                    ? Math.Max(1, from.BoundaryOutOverlap)
                    : NormalizeOverlap(modePolicy, from.BoundaryOutOverlap);
            int continuityWindow = effective == BoundaryJoinType.Continue
                ? overlap + (constraints?.ContinuityExtraFrames ?? 0)
                : 0;
            if (fallback != BoundaryFallbackReason.None && !fallbackReported)
            {
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    $"boundary-{fallback.ToString().ToLowerInvariant()}",
                    $"Clip {from.Id} boundary '{from.BoundaryOut}' falls back to a cut: {DescribeFallback(fallback)}",
                    from.Id));
            }
            if (from.BoundaryOutCarryAudio
                && effective != BoundaryJoinType.Cut
                && !targetHasGenerationStage)
            {
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "boundary-audio-carry-target-has-no-stage",
                    $"Clip {from.Id} cannot carry audio across its '{from.BoundaryOut}' boundary "
                        + "because the next clip has no generation stage to consume it.",
                    from.Id));
            }
            boundaries.Add(new BoundaryPlan(
                from.Id,
                effective,
                overlap,
                continuityWindow,
                fallback)
            {
                FrameStep = constraints?.FrameStep ?? 1,
                MinFrames = effective == BoundaryJoinType.Cut
                    ? 0
                    : constraints?.MinFrames ?? 1,
                CarryAudio = effective != BoundaryJoinType.Cut
                    && from.BoundaryOutCarryAudio
                    && targetHasGenerationStage,
            });
        }
        return new BoundaryPlanningResult(boundaries.ToImmutable(), diagnostics.ToImmutable());
    }

    internal static int NormalizeOverlap(RuleDecision rule, int authoredFrames)
    {
        if (rule.Support == RuleSupport.Unsupported || rule.Constraints is null)
        {
            return 0;
        }
        int step = Math.Max(1, rule.Constraints.FrameStep);
        int candidate = Math.Clamp(
            authoredFrames <= 0 ? rule.Constraints.DefaultFrames : authoredFrames,
            rule.Constraints.MinFrames,
            rule.Constraints.MaxFrames);
        return rule.Constraints.MinFrames
            + ((candidate - rule.Constraints.MinFrames) / step * step);
    }

    private static BoundaryFallbackReason? EvaluateTarget(
        BoundaryRuleConstraints constraints,
        ClipSpec target)
    {
        if (constraints is null)
        {
            return null;
        }
        if (constraints.TargetRequiresGeneratedEntry && target.InitVideo is not null)
        {
            return BoundaryFallbackReason.TargetHasInitVideo;
        }
        if (constraints.TargetRequiresStage && target.Stages is not { Count: > 0 })
        {
            return BoundaryFallbackReason.TargetHasNoStage;
        }
        if (constraints.TargetDisallowsInitialReference && HasExplicitFirstFrameReference(target))
        {
            return BoundaryFallbackReason.TargetHasFirstFrameReference;
        }
        return null;
    }

    private static bool HasExplicitFirstFrameReference(ClipSpec clip) =>
        clip.ImageRefs?.Any(reference => !reference.FromEnd && reference.Frame == 1) == true;

    private static string DescribeFallback(BoundaryFallbackReason fallback) => fallback switch
    {
        BoundaryFallbackReason.TargetHasInitVideo => "the next clip is init-video footage",
        BoundaryFallbackReason.TargetHasNoStage => "the next clip has no stage that can consume continuity",
        BoundaryFallbackReason.TargetHasFirstFrameReference => "the next clip has an explicit first-frame reference",
        BoundaryFallbackReason.TargetHasDerivedDuration => "the next clip's duration is derived at runtime",
        BoundaryFallbackReason.InsufficientFrameBudget => "the adjacent clips are too short for the requested overlap",
        BoundaryFallbackReason.ArchitectureRuleUnsupported => "the clip architecture does not support the requested join",
        _ => "the boundary is not applicable",
    };
}

internal sealed record BoundaryPlanningResult(
    ImmutableArray<BoundaryPlan> Boundaries,
    ImmutableArray<PlanDiagnostic> Diagnostics);
