using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal static class NoneArchitecture
{
    internal static ArchitectureId Id { get; } = new("none");
    internal static ModelProfileId ProfileId { get; } = new("none");

    internal static ArchitectureBoundaryPolicy BoundaryPolicy { get; } =
        ArchitectureBoundaryPolicy.CutOnly(
            "none",
            "Decoded init-video clips can be joined with a hard cut.");

    internal static VideoArchitectureDescriptor Descriptor { get; } = new(
        Id,
        "Decoded source only",
        [
            AudioSourceKind.Disabled,
            AudioSourceKind.Upload,
        ],
        [ArchitectureEntryMode.InitVideo],
        ArchitectureFeature.None,
        BoundaryPolicy)
    {
        ConsumesTimelineAudio = true,
        FrameGrid = 1,
        StageGuideReferences = StageGuideReferencePolicy.GeneratedOnly,
    };
}

internal sealed record NoneClipPayload : IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;
}
