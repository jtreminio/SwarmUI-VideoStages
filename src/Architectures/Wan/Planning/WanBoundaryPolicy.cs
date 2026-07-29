using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan.Planning;

/// <summary>
/// Wan joins clips with hard cuts only. Generation-time continuity and decoded crossfades both need
/// an architecture-owned boundary assembler, which this slice deliberately does not have.
/// </summary>
internal sealed class WanBoundaryPolicy : IArchitectureBoundaryPolicy
{
    internal static WanBoundaryPolicy Instance { get; } = new();

    public IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> Modes
        { get; } = new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
        {
            [BoundaryExecutionMode.Cut] = Mode(
                RuleSupport.Supported,
                "wan22.boundary.cut",
                "Decoded Wan clips can be joined with a hard cut."),
            [BoundaryExecutionMode.Continue] = Mode(
                RuleSupport.Unsupported,
                "wan22.boundary.continue.unsupported",
                "Wan has no generation-time continuity path yet."),
            [BoundaryExecutionMode.Crossfade] = Mode(
                RuleSupport.Unsupported,
                "wan22.boundary.crossfade.unsupported",
                "Wan has no decoded transition path yet."),
        };

    public IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> PublishedRules { get; }

    private WanBoundaryPolicy()
    {
        PublishedRules = new Dictionary<BoundaryExecutionMode, RuleDecision>(
            Modes.ToDictionary(pair => pair.Key, pair => pair.Value.ToRuleDecision()));
    }

    private static ArchitectureBoundaryModePolicy Mode(
        RuleSupport support,
        string code,
        string reason) =>
        new(
            support,
            code,
            reason,
            FrameStep: 1,
            MinFrames: 0,
            MaxFrames: 0,
            DefaultFrames: 0,
            ContinuityExtraFrames: 0,
            TargetRequiresGeneratedEntry: false,
            TargetRequiresStage: false,
            TargetDisallowsInitialReference: false);
}
