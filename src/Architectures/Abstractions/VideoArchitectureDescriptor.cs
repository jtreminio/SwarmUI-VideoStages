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
    /// Generated frame counts follow this grid, independent of persisted profile hints.
    /// </summary>
    public int FrameGrid { get; init; } = 1;

    /// <summary>
    /// The smallest generated frame count this architecture can express: counts are
    /// <c>k * <see cref="FrameGrid"/> + FrameGridOrigin</c>.
    /// </summary>
    public int FrameGridOrigin { get; init; } = 1;

    // Sampling may pad source footage, but published output keeps its measured length.
    public bool PreservesInitVideoFrameCount { get; init; }

    /// <summary>
    /// Architectures must opt into guide selectors beyond the generated root.
    /// </summary>
    public StageGuideReferencePolicy StageGuideReferences { get; init; } =
        StageGuideReferencePolicy.GeneratedOnly;

    /// <summary>
    /// Opts out of the generic audio overlay when the session consumes timeline tracks itself.
    /// </summary>
    public bool ConsumesTimelineAudio { get; init; }

    /// <summary>
    /// Host CreateImageToVideo passes inherit request-global video settings.
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
