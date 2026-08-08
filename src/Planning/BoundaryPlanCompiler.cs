using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;

namespace VideoStages.Planning;

internal static class BoundaryPlanCompiler
{
    internal static BoundaryPlanningResult Compile(
        IReadOnlyList<ClipSpec> authoredClips,
        IReadOnlyList<ClipPlan> clips)
    {
        ArgumentNullException.ThrowIfNull(authoredClips);
        ArgumentNullException.ThrowIfNull(clips);
        if (authoredClips.Count != clips.Count)
        {
            throw Invariant.Failure(
                "Boundary planning requires aligned authored and compiled clips.");
        }
        for (int i = 0; i < authoredClips.Count; i++)
        {
            if (authoredClips[i].Id != clips[i].ClipId)
            {
                throw Invariant.Failure(
                    "Boundary planning requires aligned authored and compiled clips.");
            }
        }

        ImmutableArray<BoundaryPlan>.Builder boundaries = ImmutableArray.CreateBuilder<BoundaryPlan>();
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        for (int i = 0; i < authoredClips.Count - 1; i++)
        {
            ClipSpec authoredFrom = authoredClips[i];
            ClipPlan from = clips[i];
            BoundaryJoinType requested = ParseJoinType(authoredFrom.BoundaryOut);
            ClipSpec authoredTo = authoredClips[i + 1];
            ClipPlan to = clips[i + 1];
            BoundaryFallbackReason fallback = BoundaryFallbackReason.None;
            bool fallbackReported = false;
            BoundaryJoinType effective = requested;
            bool targetHasGenerationStage = to.Stages.Any(stage => !stage.IsPassthrough);
            bool targetHasDerivedDuration =
                to.Audio.LengthOwner != AudioLengthOwner.Timeline;
            RuleDecision modePolicy = from.Architecture?.BoundaryPolicy
                ?.Rules.GetValueOrDefault(requested);
            BoundaryRuleConstraints constraints = modePolicy?.Constraints;
            if (requested != BoundaryJoinType.Cut
                && from.Architecture is not null
                && to.Architecture is not null
                && from.Architecture.Id != to.Architecture.Id)
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.ArchitectureRuleUnsupported;
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "boundary-cross-architecture-non-cut",
                    $"Clip {authoredFrom.Id} boundary '{authoredFrom.BoundaryOut}' is invalid between "
                        + $"architecture '{from.Architecture.Id}' and "
                        + $"'{to.Architecture.Id}'. Cross-architecture boundaries must be cuts.",
                    authoredFrom.Id));
                fallbackReported = true;
            }
            else if (requested != BoundaryJoinType.Cut
                && modePolicy is { Support: RuleSupport.Unsupported })
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.ArchitectureRuleUnsupported;
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    modePolicy.Code,
                    modePolicy.Reason,
                    authoredFrom.Id));
                fallbackReported = true;
            }
            else if (requested != BoundaryJoinType.Cut
                && EvaluateTarget(constraints, authoredTo, to) is { } targetFallback)
            {
                effective = BoundaryJoinType.Cut;
                fallback = targetFallback;
            }
            else if (requested == BoundaryJoinType.Continue
                && !targetHasGenerationStage)
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.TargetHasNoStage;
            }
            else if (requested == BoundaryJoinType.Continue
                && targetHasDerivedDuration)
            {
                effective = BoundaryJoinType.Cut;
                fallback = BoundaryFallbackReason.TargetHasDerivedDuration;
            }

            int requestedWindow = effective == BoundaryJoinType.Cut
                ? 0
                : modePolicy is null
                    ? Math.Max(1, authoredFrom.BoundaryOutOverlap)
                    : NormalizeWindow(modePolicy, authoredFrom.BoundaryOutOverlap);
            bool referenceContinue = effective == BoundaryJoinType.Continue
                && constraints?.ContinueMode == ContinueBoundaryMode.Reference;
            int overlap = referenceContinue ? 0 : requestedWindow;
            int continuityWindow = effective == BoundaryJoinType.Continue
                ? requestedWindow + (referenceContinue
                    ? 0
                    : constraints?.ContinuityExtraFrames ?? 0)
                : 0;
            if (fallback != BoundaryFallbackReason.None && !fallbackReported)
            {
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    $"boundary-{fallback.ToString().ToLowerInvariant()}",
                    $"Clip {authoredFrom.Id} boundary '{authoredFrom.BoundaryOut}' falls back to a cut: {DescribeFallback(fallback)}",
                    authoredFrom.Id));
            }
            if (authoredFrom.BoundaryOutCarryAudio
                && effective != BoundaryJoinType.Cut
                && !targetHasGenerationStage)
            {
                diagnostics.Add(new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "boundary-audio-carry-target-has-no-stage",
                    $"Clip {authoredFrom.Id} cannot carry audio across its '{authoredFrom.BoundaryOut}' boundary "
                        + "because the next clip has no generation stage to consume it.",
                    authoredFrom.Id));
            }
            boundaries.Add(new BoundaryPlan(
                authoredFrom.Id,
                effective,
                overlap,
                continuityWindow,
                fallback)
            {
                ContinueMode = constraints?.ContinueMode ?? ContinueBoundaryMode.Overlap,
                FrameStep = constraints?.FrameStep ?? 1,
                MinFrames = effective == BoundaryJoinType.Cut
                    ? 0
                    : constraints?.MinFrames ?? 1,
                CarryAudio = effective != BoundaryJoinType.Cut
                    && !referenceContinue
                    && authoredFrom.BoundaryOutCarryAudio
                    && targetHasGenerationStage,
                ReferenceScale = authoredFrom.BoundaryOutReferenceScale,
                ReferenceIncludeSoundtrack =
                    authoredFrom.BoundaryOutReferenceIncludeSoundtrack,
            });
        }
        return new BoundaryPlanningResult(boundaries.ToImmutable(), diagnostics.ToImmutable());
    }

    internal static int NormalizeWindow(RuleDecision rule, int authoredFrames)
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
        ClipSpec authoredTarget,
        ClipPlan target)
    {
        if (constraints is null)
        {
            return null;
        }
        if (constraints.TargetRequiresGeneratedEntry
            && target.EntryMode == ArchitectureEntryMode.InitVideo)
        {
            return BoundaryFallbackReason.TargetHasInitVideo;
        }
        if (constraints.TargetRequiresStage && target.Stages.Count == 0)
        {
            return BoundaryFallbackReason.TargetHasNoStage;
        }
        if (constraints.TargetDisallowsInitialReference
            && authoredTarget.FrameRefs?.Any(reference => reference.IsOpeningFrame) == true)
        {
            return BoundaryFallbackReason.TargetHasFirstFrameReference;
        }
        return null;
    }

    internal static BoundaryJoinType ParseJoinType(string value)
    {
        if (string.Equals(value, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase))
        {
            return BoundaryJoinType.Continue;
        }
        if (string.Equals(value, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase))
        {
            return BoundaryJoinType.Crossfade;
        }
        return BoundaryJoinType.Cut;
    }

    private static string DescribeFallback(BoundaryFallbackReason fallback) => fallback switch
    {
        BoundaryFallbackReason.TargetHasInitVideo => "the next clip is init-video footage",
        BoundaryFallbackReason.TargetHasNoStage => "the next clip has no stage that can consume continuity",
        BoundaryFallbackReason.TargetHasFirstFrameReference => "the next clip has an explicit first keyframe",
        BoundaryFallbackReason.TargetHasDerivedDuration => "the next clip's duration is derived at runtime",
        BoundaryFallbackReason.InsufficientFrameBudget => "the adjacent clips are too short for the requested overlap",
        BoundaryFallbackReason.ArchitectureRuleUnsupported => "the clip architecture does not support the requested join",
        _ => "the boundary is not applicable",
    };
}

internal sealed record BoundaryPlanningResult(
    ImmutableArray<BoundaryPlan> Boundaries,
    ImmutableArray<PlanDiagnostic> Diagnostics);
