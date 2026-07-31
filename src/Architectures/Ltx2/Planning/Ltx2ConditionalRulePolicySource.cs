using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Single source of truth for LTX conditional behavior. The same typed policies publish catalog
/// rules and produce backend diagnostics.
/// </summary>
internal static class Ltx2ConditionalRulePolicySource
{
    internal static RuleDecision AudioReuseRequiresThreeStages { get; } =
        RuleDecision.Conditional(
            ArchitectureFeatureVocabulary.RuleCode(
                ConditionalRuleCodeId.AudioReuseRequiresStages),
            "Audio reuse needs at least three active stages: generate, capture, then reuse.",
            RuleScope.Clip,
            new MinimumActiveStagesRuleConstraints(3));

    internal static RuleDecision PromptRelayRequiresFixedLength { get; } =
        RuleDecision.Conditional(
            ArchitectureFeatureVocabulary.RuleCode(
                ConditionalRuleCodeId.PromptRelayRequiresFixedLength),
            "Prompt relay requires a fixed frame count and cannot be combined with audio-owned or ControlNet-owned clip length.",
            RuleScope.Clip);

    internal static RuleDecision RetakeAndReferencesAreExclusive { get; } =
        RuleDecision.Conditional(
            ArchitectureFeatureVocabulary.RuleCode(
                ConditionalRuleCodeId.RetakeExcludesReferences),
            "Retake and frame references are mutually exclusive because guide merging would overwrite the retake mask.",
            RuleScope.Stage,
            new MutuallyExclusiveRuleConstraints(
                [ConditionalRuleFeature.Retake, ConditionalRuleFeature.FrameReferences]));

    internal static RuleDecision RetakeRequiresSource { get; } =
        RuleDecision.Conditional(
            ArchitectureFeatureVocabulary.RuleCode(
                ConditionalRuleCodeId.RetakeRequiresSource),
            "Retake requires a sourced clip or a global Refine Video source.",
            RuleScope.Clip,
            new RequiredEntryModesRuleConstraints(
                [ArchitectureEntryMode.SourceVideo, ArchitectureEntryMode.RefineVideo]));

    /// <summary>
    /// Every threshold below is read back out of the published rule, so the catalog value and the
    /// value the evaluator enforces are the same number by construction.
    /// </summary>
    internal static int AudioReuseMinimumActiveStages { get; } = AudioReuseRequiresThreeStages
        .Require<MinimumActiveStagesRuleConstraints>().MinimumActiveStages;

    internal static IReadOnlyList<ArchitectureEntryMode> RetakeEntryModes { get; } =
        RetakeRequiresSource.Require<RequiredEntryModesRuleConstraints>().RequiresAnyEntryMode;

    internal static IReadOnlyList<RuleDecision> PublishedRules { get; } =
    [
        AudioReuseRequiresThreeStages,
        PromptRelayRequiresFixedLength,
        RetakeAndReferencesAreExclusive,
        RetakeRequiresSource,
    ];

    internal static IReadOnlyList<PlanDiagnostic> Validate(
        IReadOnlyList<ClipPlan> clips)
    {
        List<PlanDiagnostic> diagnostics = [];
        foreach (ClipPlan clip in clips)
        {
            if (clip.ArchitecturePayload is Ltx2ClipPayload ltxClip
                && ltxClip.AudioReuse.IsRequested
                && !ltxClip.AudioReuse.IsEligible)
            {
                diagnostics.Add(Diagnostic(
                    AudioReuseRequiresThreeStages,
                    PlanDiagnosticSeverity.Warning,
                    clip.ClipId));
            }
            if (clip.Audio.Length.Owner is AudioLengthOwner.Audio or AudioLengthOwner.ControlNet
                && clip.Stages.Any(stage =>
                    stage.ArchitecturePayload is Ltx2StagePayload payload
                    && !payload.PromptRelay.AuthoredWindows.IsDefaultOrEmpty))
            {
                diagnostics.Add(Error(PromptRelayRequiresFixedLength, clip.ClipId));
            }

            foreach (StagePlan stage in clip.Stages)
            {
                if (stage.ArchitecturePayload is not Ltx2StagePayload payload)
                {
                    continue;
                }
                if (payload.Retake is not null && !payload.FrameReferences.IsDefaultOrEmpty)
                {
                    diagnostics.Add(Error(
                        RetakeAndReferencesAreExclusive,
                        clip.ClipId,
                        stage.StageId));
                }
                if (payload.Retake is not null
                    && !RetakeEntryModes.Contains(clip.EntryMode))
                {
                    diagnostics.Add(Error(RetakeRequiresSource, clip.ClipId, stage.StageId));
                }
            }
        }

        return diagnostics.AsReadOnly();
    }

    private static PlanDiagnostic Error(
        RuleDecision rule,
        int? clipId = null,
        int? stageId = null) =>
        Diagnostic(rule, PlanDiagnosticSeverity.Error, clipId, stageId);

    private static PlanDiagnostic Diagnostic(
        RuleDecision rule,
        PlanDiagnosticSeverity severity,
        int? clipId = null,
        int? stageId = null) =>
        new(
            severity,
            rule.Code,
            rule.Reason,
            clipId,
            stageId);
}
