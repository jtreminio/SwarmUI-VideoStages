using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal static class NoneArchitecture
{
    internal static ArchitectureId Id { get; } = new("none");
    internal static ModelProfileId ProfileId { get; } = new("none");

    /// <summary>Cut-only, owned here and published from the same modes the compiler reads.</summary>
    internal static IArchitectureBoundaryPolicy BoundaryPolicy { get; } =
        ArchitectureBoundaryPolicy.CutOnly(
            "none",
            "Decoded sourced clips can be joined with a hard cut.",
            "A sourced-only clip has no architecture stage that can consume continuity.",
            "Architecture-neutral sourced clips currently support cut joins only.");

    internal static VideoArchitectureDescriptor Descriptor { get; } = new(
        Id,
        "Decoded source only",
        [
            AudioSourceKind.Disabled,
            AudioSourceKind.Upload,
        ],
        [ArchitectureEntryMode.SourceVideo],
        new(
            ArchitectureCapability.SourcedEntry | ArchitectureCapability.DecodedOutput,
            ClipCapability.SourceVideo
                | ClipCapability.AudioSources
                | ClipCapability.AudioSegments,
            StageCapability.None),
        BoundaryPolicy)
    {
        FrameGrid = 1,
        StageGuideReferences = StageGuideReferencePolicy.GeneratedOnly,
    };
}

internal sealed class NoneArchitectureModule : IVideoArchitectureModule
{
    internal static NoneArchitectureModule Instance { get; } = new();

    public VideoArchitectureDescriptor Descriptor => NoneArchitecture.Descriptor;

    public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
    {
        resolved = null;
        return false;
    }

    public ArchitectureClipCompilation ValidateAndCompileClip(
        ClipSpec clip,
        IReadOnlyDictionary<int, ResolvedVideoModel> stageModels,
        ArchitectureClipCompileContext context) =>
        new(
            new NoneClipPayload(clip.Id),
            new Dictionary<int, IArchitectureStagePayload>(),
            []);
}

internal sealed record NoneClipPayload(int ClipId) : IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;
}
