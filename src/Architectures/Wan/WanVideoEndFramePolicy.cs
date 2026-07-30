using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Wan;

/// <summary>
/// Maps SwarmUI's one request-global final image to the only unambiguous WAN timeline target.
/// This decision uses executable clip shape and host model facts rather than profile aliases.
/// </summary>
internal static class WanVideoEndFramePolicy
{
    internal static bool TryResolveTarget(
        VideoExecutionPlan plan,
        out ClipPlan clip,
        out StagePlan stage)
    {
        clip = null;
        stage = null;
        if (plan?.Clips is not { Count: 1 })
        {
            return false;
        }
        ClipPlan onlyClip = plan.Clips[0];
        if (onlyClip.Architecture?.Id != WanArchitectureModule.ArchitectureId
            || onlyClip.EntryMode != ArchitectureEntryMode.ImageToVideo
            || onlyClip.Input != ClipInputKind.RootMedia
            || onlyClip.IsSourced
            || onlyClip.SourceVideo is not null)
        {
            return false;
        }
        StagePlan terminalGeneratingStage = onlyClip.Stages
            .LastOrDefault(candidate => !candidate.IsPassthrough);
        if (terminalGeneratingStage is null
            || !SupportsHostEndFrame(terminalGeneratingStage.ResolvedModel))
        {
            return false;
        }
        clip = onlyClip;
        stage = terminalGeneratingStage;
        return true;
    }

    internal static bool ShouldApply(
        VideoExecutionPlan plan,
        ClipPlan clip,
        StagePlan stage) =>
        TryResolveTarget(plan, out ClipPlan targetClip, out StagePlan targetStage)
        && ReferenceEquals(clip, targetClip)
        && ReferenceEquals(stage, targetStage);

    private static bool SupportsHostEndFrame(ResolvedVideoModel model) =>
        model is
        {
            ArchitectureId: var architectureId,
            CompatibilityClassId: var compatibility,
            EntryAbilities: var abilities,
        }
        && architectureId == WanArchitectureModule.ArchitectureId
        && abilities.HasFlag(VideoModelEntryAbility.ImageToVideo)
        && WanArchitectureModule.SupportsHostEndFrame(compatibility);
}
