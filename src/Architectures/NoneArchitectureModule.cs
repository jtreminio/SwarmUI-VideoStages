using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures;

internal static class NoneArchitecture
{
    internal static ArchitectureId Id { get; } = new("none");
    internal static ModelProfileId ProfileId { get; } = new("none");

    internal static VideoArchitectureDescriptor Descriptor { get; } = new(
        Id,
        "Decoded source only",
        ProfileId,
        [ArchitectureEntryMode.SourceVideo],
        [
            AudioSourceKind.Disabled,
            AudioSourceKind.Upload,
        ],
        [
            new(
                ProfileId,
                "Decoded source only",
                ModelProfileCapability.None,
                [])
        ],
        new(
            ArchitectureCapability.SourcedEntry | ArchitectureCapability.DecodedOutput,
            ClipCapability.SourceVideo
                | ClipCapability.AudioSources
                | ClipCapability.AudioSegments,
            StageCapability.None,
            OutputCapability.Video | OutputCapability.AttachedAudio),
        new Dictionary<BoundaryExecutionMode, RuleDecision>
        {
            [BoundaryExecutionMode.Cut] = RuleDecision.Supported(
                "none.boundary.cut",
                "Decoded sourced clips can be joined with a hard cut.",
                RuleScope.Boundary),
            [BoundaryExecutionMode.Continue] = RuleDecision.Unsupported(
                "none.boundary.continue.unsupported",
                "A sourced-only clip has no architecture stage that can consume continuity.",
                RuleScope.Boundary),
            [BoundaryExecutionMode.Crossfade] = RuleDecision.Unsupported(
                "none.boundary.crossfade.unsupported",
                "Architecture-neutral sourced clips currently support cut joins only.",
                RuleScope.Boundary),
        });
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
        new(new NoneClipPayload(clip.Id), []);
}

internal sealed record NoneClipPayload(int ClipId) : IArchitectureClipPayload
{
    public ArchitectureId ArchitectureId => NoneArchitecture.Id;
}
