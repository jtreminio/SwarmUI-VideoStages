using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

/// <summary>
/// Resolves the single architecture allowed to own host-root transformations. Sourced clip stages
/// consume their own media, so they cannot claim the host root merely by appearing first.
/// </summary>
internal static class ArchitectureRootOwnerResolver
{
    internal static ArchitectureId? Resolve(VideoExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ClipPlan owner = plan.Clips.FirstOrDefault(clip =>
            clip.Stages.Count > 0
            && clip.Input is ClipInputKind.RootMedia or ClipInputKind.EmptyLatent);
        if (owner is null)
        {
            return null;
        }
        return owner.Architecture?.Id
            ?? throw new InvalidOperationException(
                $"Root-owning clip {owner.ClipId} has no architecture identity.");
    }
}
