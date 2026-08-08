using VideoStages.Planning;

namespace VideoStages.Architectures.Abstractions;

internal sealed record VideoArchitectureDescriptor(
    ArchitectureId Id,
    string DisplayName,
    IReadOnlyList<AudioSourceKind> AudioSourceKinds,
    IReadOnlyList<ArchitectureEntryMode> EntryModes,
    ArchitectureFeature Features,
    ArchitectureBoundaryPolicy BoundaryPolicy)
{
    /// <summary>
    /// The generated-frame grid shared by this architecture. It is an architecture/runtime fact,
    /// not authorization attached to a persisted profile alias.
    /// </summary>
    public int FrameGrid { get; init; } = 1;

    /// <summary>
    /// The smallest generated frame count this architecture can express: counts are
    /// <c>k * <see cref="FrameGrid"/> + FrameGridOrigin</c>.
    /// </summary>
    public int FrameGridOrigin { get; init; } = 1;

    /// <summary>
    /// The effective stage guide selectors this architecture can execute. Fail closed so a new
    /// architecture must opt into every selector beyond the generated root.
    /// </summary>
    public StageGuideReferencePolicy StageGuideReferences { get; init; } =
        StageGuideReferencePolicy.GeneratedOnly;

    /// <summary>
    /// True when this architecture consumes the authored timeline audio tracks itself — it
    /// conditions generation on them or muxes them inside its own session. Every other
    /// architecture gets the generic post-decode overlay, so audio tracks play over any video
    /// model without the model knowing about them.
    /// </summary>
    public bool ConsumesTimelineAudio { get; init; }

    /// <summary>
    /// True when this architecture's stages are sampled by the host's own <c>CreateImageToVideo</c>
    /// pass, so request-global video settings reach the graph unless the architecture nulls them
    /// out. Every other architecture builds its own sampler and never sees them.
    /// </summary>
    public bool RunsOnStockHostSampler { get; init; }

    public IReadOnlyDictionary<BoundaryJoinType, RuleDecision> BoundaryRules =>
        BoundaryPolicy.Rules;
}

/// <summary>A host model bound to the architecture that claimed it.</summary>
/// <remarks>
/// <see cref="LorasTargetTextEncoder"/> is a core-owned fact from the resolved model
/// compatibility: false means normal LoRAs must not become effective solely through their
/// text-encoder weight.
/// </remarks>
internal sealed record ResolvedVideoModel(
    string ModelName,
    ModelProfileId ModelProfileId,
    VideoArchitectureDescriptor Architecture,
    string ModelClassId,
    string CompatibilityClassId,
    IReadOnlyList<FrameReferencePosition> FrameReferencePositions,
    bool LorasTargetTextEncoder)
{
    public ArchitectureId ArchitectureId => Architecture.Id;

    public int FrameGrid => Architecture.FrameGrid;

    public int FrameGridOrigin => Architecture.FrameGridOrigin;

    public LoraTarget LoraTarget => LorasTargetTextEncoder
        ? LoraTarget.ModelAndTextEncoder
        : LoraTarget.ModelOnly;
}
