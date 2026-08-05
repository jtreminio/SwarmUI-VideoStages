using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>
/// Resolves the single architecture allowed to own host-root transformations. InitVideo clip stages
/// consume their own media, so they cannot claim the host root merely by appearing first.
/// </summary>
internal static class ArchitectureRootOwnerResolver
{
    internal static ArchitectureId? Resolve(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ClipPlan owner = FindOwner(plan);
        if (owner is null)
        {
            return null;
        }
        return owner.Architecture?.Id
            ?? throw Invariant.Failure(
                $"Root-owning clip {owner.ClipId} has no architecture identity.");
    }

    internal static bool TryResolve(
        VideoExecutionPlan plan,
        out ArchitectureId? architectureId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ClipPlan owner = FindOwner(plan);
        architectureId = owner?.Architecture?.Id;
        return owner is null || architectureId.HasValue;
    }

    private static ClipPlan FindOwner(VideoExecutionPlan plan) =>
        plan.Clips.FirstOrDefault(clip =>
            clip.Stages.Count > 0
            && clip.EntryMode != ArchitectureEntryMode.InitVideo);
}
