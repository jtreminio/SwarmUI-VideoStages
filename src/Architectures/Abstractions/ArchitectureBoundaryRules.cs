using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

internal enum RuleSupport
{
    Supported,
    Unsupported,
    Conditional,
}

internal enum ContinueBoundaryMode
{
    Overlap,
    Reference,
}

internal sealed record BoundaryRuleConstraints(
    int FrameStep,
    int MinFrames,
    int MaxFrames,
    int DefaultFrames,
    int ContinuityExtraFrames,
    bool TargetRequiresGeneratedEntry,
    bool TargetRequiresStage,
    bool TargetDisallowsInitialReference)
{
    public ContinueBoundaryMode ContinueMode { get; init; } = ContinueBoundaryMode.Overlap;
}

internal sealed record RuleDecision(
    RuleSupport Support,
    string Code,
    string Reason,
    BoundaryRuleConstraints Constraints = null)
{
    public static RuleDecision Supported(string code, string reason) =>
        new(RuleSupport.Supported, code, reason);

    public static RuleDecision Unsupported(string code, string reason) =>
        new(RuleSupport.Unsupported, code, reason);

    public static RuleDecision Conditional(
        string code,
        string reason,
        BoundaryRuleConstraints constraints) =>
        new(RuleSupport.Conditional, code, reason, constraints);
}

/// <summary>
/// The single owner of an architecture's boundary behavior: catalog publication and plan
/// compilation read the same typed rules.
/// </summary>
internal sealed class ArchitectureBoundaryPolicy
{
    internal ArchitectureBoundaryPolicy(
        IReadOnlyDictionary<BoundaryJoinType, RuleDecision> rules)
    {
        Rules = new Dictionary<BoundaryJoinType, RuleDecision>(
            rules ?? throw new ArgumentNullException(nameof(rules)));
    }

    internal IReadOnlyDictionary<BoundaryJoinType, RuleDecision> Rules { get; }

    internal static ArchitectureBoundaryPolicy CutOnly(
        string codePrefix,
        string cutReason,
        string continueReason = "This architecture has no continuity path.",
        string crossfadeReason = "This architecture has no decoded transition path.") =>
        new(new Dictionary<BoundaryJoinType, RuleDecision>
        {
            [BoundaryJoinType.Cut] = RuleDecision.Supported(
                $"{codePrefix}.boundary.cut",
                cutReason),
            [BoundaryJoinType.Continue] = RuleDecision.Unsupported(
                $"{codePrefix}.boundary.continue.unsupported",
                continueReason),
            [BoundaryJoinType.Crossfade] = RuleDecision.Unsupported(
                $"{codePrefix}.boundary.crossfade.unsupported",
                crossfadeReason),
        });
}
