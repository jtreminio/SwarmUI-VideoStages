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
        new ArchitectureBoundaryPolicy(
            new Dictionary<BoundaryExecutionMode, ArchitectureBoundaryModePolicy>
            {
                [BoundaryExecutionMode.Cut] = Mode(
                    RuleSupport.Supported,
                    "none.boundary.cut",
                    "Decoded sourced clips can be joined with a hard cut."),
                [BoundaryExecutionMode.Continue] = Mode(
                    RuleSupport.Unsupported,
                    "none.boundary.continue.unsupported",
                    "A sourced-only clip has no architecture stage that can consume continuity."),
                [BoundaryExecutionMode.Crossfade] = Mode(
                    RuleSupport.Unsupported,
                    "none.boundary.crossfade.unsupported",
                    "Architecture-neutral sourced clips currently support cut joins only."),
            });

    internal static VideoArchitectureDescriptor Descriptor { get; } = new(
        Id,
        "Decoded source only",
        ProfileId,
        [
            AudioSourceKind.Disabled,
            AudioSourceKind.Upload,
        ],
        [
            new(
                ProfileId,
                "Decoded source only",
                [ArchitectureEntryMode.SourceVideo],
                [])
        ],
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

    private static ArchitectureBoundaryModePolicy Mode(
        RuleSupport support,
        string code,
        string reason) =>
        new(
            support,
            code,
            reason,
            FrameStep: 1,
            MinFrames: 0,
            MaxFrames: 0,
            DefaultFrames: 0,
            ContinuityExtraFrames: 0,
            TargetRequiresGeneratedEntry: false,
            TargetRequiresStage: false,
            TargetDisallowsInitialReference: false);
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
