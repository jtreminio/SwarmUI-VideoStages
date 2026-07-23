using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Validates option combinations owned by the LTX runtime.</summary>
internal static class Ltx2VideoPlanValidationCompiler
{
    internal static IReadOnlyList<VideoPlanDiagnostic> Validate(
        IReadOnlyList<ClipPlan> clips,
        IReadOnlyList<ClipPlan> timelineClips,
        RootPlan root) =>
        Ltx2ConditionalRulePolicySource.Validate(clips, timelineClips, root);
}
