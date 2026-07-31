using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>LTX boundary behavior; the catalog projection is derived from these modes.</summary>
internal static class Ltx2BoundaryPolicy
{
    internal const int DefaultFrames = Ltx2ArchitectureModule.FrameGrid;
    internal const int MaxFrames = 48;

    internal static ArchitectureBoundaryPolicy Instance { get; } =
        new(new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
        {
            [BoundaryExecutionMode.Cut] = ArchitectureBoundaryModePolicy.Supported(
                "ltx2.boundary.cut",
                "Decoded LTX clips can be joined with a hard cut."),
            [BoundaryExecutionMode.Continue] = ArchitectureBoundaryModePolicy.Conditional(
                "ltx2.boundary.continue",
                "Continue requires adjacent LTX clips and a compatible generated target.",
                new BoundaryRuleConstraints(
                    FrameStep: Ltx2ArchitectureModule.FrameGrid,
                    MinFrames: DefaultFrames,
                    MaxFrames: MaxFrames,
                    DefaultFrames: DefaultFrames,
                    ContinuityExtraFrames: 1,
                    TargetRequiresGeneratedEntry: true,
                    TargetRequiresStage: true,
                    TargetDisallowsInitialReference: true)),
            [BoundaryExecutionMode.Crossfade] = ArchitectureBoundaryModePolicy.Conditional(
                "ltx2.boundary.crossfade",
                "Crossfade currently uses the LTX-owned decoded transition path.",
                new BoundaryRuleConstraints(
                    FrameStep: Ltx2ArchitectureModule.FrameGrid,
                    MinFrames: DefaultFrames,
                    MaxFrames: MaxFrames,
                    DefaultFrames: DefaultFrames,
                    ContinuityExtraFrames: 0,
                    TargetRequiresGeneratedEntry: false,
                    TargetRequiresStage: false,
                    TargetDisallowsInitialReference: false)),
        });
}
