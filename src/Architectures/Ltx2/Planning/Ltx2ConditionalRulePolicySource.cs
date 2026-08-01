using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Single source of truth for LTX conditional behavior. The same typed policies publish catalog
/// rules and produce backend diagnostics.
/// </summary>
internal static class Ltx2ConditionalRulePolicySource
{
    /// <summary>
    /// Audio reuse needs generate, capture, then reuse. Falling short is not an authoring
    /// decision to gate or report, so this stays a compile-time eligibility fact.
    /// </summary>
    internal const int AudioReuseMinimumActiveStages = 3;

    internal static IReadOnlyList<ArchitectureEntryMode> RetakeEntryModes { get; } =
        [ArchitectureEntryMode.InitVideo];

    internal static RuleDecision RetakeRequiresSource { get; } =
        RuleDecision.Conditional(
            ArchitectureFeatureVocabulary.RuleCode(
                ConditionalRuleCodeId.RetakeRequiresSource),
            "Retake requires an init-video clip.",
            RuleScope.Clip);

    internal static IReadOnlyList<RuleDecision> PublishedRules { get; } =
    [
        RetakeRequiresSource,
    ];

    internal static IReadOnlyList<PlanDiagnostic> Validate(
        IReadOnlyList<ClipPlan> clips)
    {
        List<PlanDiagnostic> diagnostics = [];
        foreach (ClipPlan clip in clips)
        {
            foreach (StagePlan stage in clip.Stages)
            {
                if (stage.ArchitecturePayload is not Ltx2StagePayload payload)
                {
                    continue;
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
        new(
            PlanDiagnosticSeverity.Error,
            rule.Code,
            rule.Reason,
            clipId,
            stageId);
}
