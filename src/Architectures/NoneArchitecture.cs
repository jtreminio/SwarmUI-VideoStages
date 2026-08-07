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

    /// <summary>There is no module to compile through, so every `none` clip plans to nothing.</summary>
    internal static ArchitectureClipCompilation EmptyCompilation { get; } = new(
        new NoneClipPayload(),
        new Dictionary<int, IArchitectureStagePayload>(),
        []);

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

/// <summary>The `none` architecture has no module, so it compiles to an empty clip payload.</summary>
internal sealed record NoneClipPayload : IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;
}
