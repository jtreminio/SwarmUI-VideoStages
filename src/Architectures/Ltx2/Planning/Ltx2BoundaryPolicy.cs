using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>LTX boundary behavior and its public catalog projection.</summary>
internal sealed class Ltx2BoundaryPolicy : IArchitectureBoundaryPolicy
{
    internal const int DefaultFrames = 8;
    internal const int MaxFrames = 48;

    internal static Ltx2BoundaryPolicy Instance { get; } = new();

    public IReadOnlyDictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy> Modes
        { get; } = new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
        {
            [BoundaryExecutionMode.Cut] = new(
                RuleSupport.Supported,
                "ltx2.boundary.cut",
                "Decoded LTX clips can be joined with a hard cut.",
                FrameStep: 1,
                MinFrames: 0,
                MaxFrames: 0,
                DefaultFrames: 0,
                ContinuityExtraFrames: 0,
                TargetRequiresGeneratedEntry: false,
                TargetRequiresStage: false,
                TargetDisallowsInitialReference: false),
            [BoundaryExecutionMode.Continue] = new(
                RuleSupport.Conditional,
                "ltx2.boundary.continue",
                "Continue requires adjacent LTX clips and a compatible generated target.",
                FrameStep: 8,
                MinFrames: DefaultFrames,
                MaxFrames: MaxFrames,
                DefaultFrames: DefaultFrames,
                ContinuityExtraFrames: 1,
                TargetRequiresGeneratedEntry: true,
                TargetRequiresStage: true,
                TargetDisallowsInitialReference: true),
            [BoundaryExecutionMode.Crossfade] = new(
                RuleSupport.Conditional,
                "ltx2.boundary.crossfade",
                "Crossfade currently uses the LTX-owned decoded transition path.",
                FrameStep: 8,
                MinFrames: DefaultFrames,
                MaxFrames: MaxFrames,
                DefaultFrames: DefaultFrames,
                ContinuityExtraFrames: 0,
                TargetRequiresGeneratedEntry: false,
                TargetRequiresStage: false,
                TargetDisallowsInitialReference: false),
        };

    public IReadOnlyDictionary<BoundaryExecutionMode, RuleDecision> PublishedRules { get; }

    private Ltx2BoundaryPolicy()
    {
        PublishedRules = new Dictionary<BoundaryExecutionMode, RuleDecision>(
            Modes.ToDictionary(pair => pair.Key, pair => pair.Value.ToRuleDecision()));
    }
}
