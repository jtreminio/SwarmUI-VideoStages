using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>LTX boundary behavior; the catalog projection is derived from these modes.</summary>
internal static class Ltx2BoundaryPolicy
{
    internal const int DefaultFrames = Ltx2ArchitectureModule.FrameGrid;
    internal const int MaxFrames = 48;

    internal static ArchitectureBoundaryPolicy Instance { get; } =
        new(new Dictionary<BoundaryJoinType, RuleDecision>
        {
            [BoundaryJoinType.Cut] = RuleDecision.Supported(
                "ltx2.boundary.cut",
                "Decoded LTX clips can be joined with a hard cut."),
            [BoundaryJoinType.Continue] = RuleDecision.Conditional(
                "ltx2.boundary.continue",
                "Continue requires adjacent LTX clips and a compatible generated target.",
                new BoundaryRuleConstraints(
                    FrameStep: Ltx2ArchitectureModule.FrameGrid,
                    MinFrames: Ltx2ArchitectureModule.FrameGrid,
                    MaxFrames: MaxFrames,
                    DefaultFrames: DefaultFrames,
                    ContinuityExtraFrames: 1,
                    TargetRequiresGeneratedEntry: true,
                    TargetRequiresStage: true,
                    TargetDisallowsInitialReference: true)),
            [BoundaryJoinType.Crossfade] = RuleDecision.Conditional(
                "ltx2.boundary.crossfade",
                "Decoded LTX clips can be crossfaded.",
                new BoundaryRuleConstraints(
                    FrameStep: Ltx2ArchitectureModule.FrameGrid,
                    MinFrames: Ltx2ArchitectureModule.FrameGrid,
                    MaxFrames: MaxFrames,
                    DefaultFrames: DefaultFrames,
                    ContinuityExtraFrames: 0,
                    TargetRequiresGeneratedEntry: false,
                    TargetRequiresStage: false,
                    TargetDisallowsInitialReference: false)),
        });
}
