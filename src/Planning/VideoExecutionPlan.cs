using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

/// <summary>
/// An immutable, graph-independent description of the timeline that VideoStages will run.
/// It deliberately contains no <c>WorkflowGenerator</c>, node, or media references: compiling a
/// plan must be safe to do before the workflow graph is built.
/// </summary>
internal sealed record VideoExecutionPlan(
    int Width,
    int Height,
    int FramesPerSecond,
    RootPlan Root,
    IReadOnlyList<ClipPlan> Clips,
    IReadOnlyList<BoundaryPlan> Boundaries,
    IReadOnlyList<PlanDiagnostic> Diagnostics)
{
    /// <summary>Whether the author explicitly configured both timeline dimensions.</summary>
    public bool HasConfiguredResolution { get; init; } = true;

    /// <summary>
    /// Unified projection of clip base audio, compatibility overlays, and
    /// root-authored timeline-wide audio segments. The executable per-clip
    /// slices also live in each <see cref="ClipPlan.Audio"/>.
    /// </summary>
    public AudioTimelinePlan AudioTimeline { get; init; } = AudioTimelinePlan.Empty;
}

/// <summary>What the timeline does with the host's pre-VideoStages result.</summary>
internal sealed record RootPlan(
    HostRootKind HostKind,
    RootUse Use,
    HostCoreDisposition CoreDisposition,
    TimelineOutputDisposition OutputDisposition,
    NativeAudioDisposition NativeAudioDisposition);

internal enum HostRootKind
{
    ImageToVideo,
    TextToVideoRoot,
    GlobalRefineSource,
}

/// <summary>Who consumes the host root media, independently from what happens to host core nodes.</summary>
internal enum RootUse
{
    None,
    ClipZeroSeed,
    GeneratedClipDonor,
    GlobalRefineReplacement,
    Discard,
}

internal enum HostCoreDisposition
{
    Keep,
    Handoff,
    Drop,
}

/// <summary>Who owns final publication, independent of whether host core nodes are kept alive.</summary>
internal enum TimelineOutputDisposition
{
    PreserveHostOutput,
    PublishTimelineOutput,
}

internal enum NativeAudioDisposition
{
    KeepHostAudio,
    MakeAvailableToTimeline,
    UseGlobalRefineAudio,
    DiscardWithRoot,
}

/// <summary>
/// The narrow bit of host state required to plan root ownership. It holds only immutable facts;
/// runtime media and graph nodes remain outside the plan compiler.
/// </summary>
internal sealed record RootEnvironment(
    HostRootKind HostKind,
    bool CanHandoffHostCore = false,
    bool HasGlobalRefineSource = false)
{
    public static RootEnvironment FromSpec(VideoStagesSpec spec) => new(
        spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo);
}

/// <summary>The primary artifact that starts a clip before its stages run.</summary>
internal enum ClipInputKind
{
    RootMedia,
    EmptyLatent,
    SourceVideo,
}

internal sealed record ClipPlan(
    int ClipId,
    int? Frames,
    ClipInputKind Input,
    bool IsSourced,
    SourceVideoPlan SourceVideo,
    IReadOnlyList<StagePlan> Stages,
    AudioPlan Audio)
{
    /// <summary>The one architecture established for this clip before graph mutation begins.</summary>
    public VideoArchitectureDescriptor Architecture { get; init; }

    /// <summary>
    /// How this clip enters the timeline. Resolved once during compilation so plan validators do
    /// not each re-derive it from root disposition and sourcing.
    /// </summary>
    public ArchitectureEntryMode EntryMode { get; init; }

    public IArchitectureClipPayload ArchitecturePayload { get; init; }
}

internal sealed record SourceVideoPlan(
    string Data,
    string FileName,
    double StartSeconds,
    int TargetWidth,
    int TargetHeight,
    int TargetFramesPerSecond);

/// <summary>An architecture-neutral stage dispatch instruction.</summary>
internal sealed record StagePlan(
    int StageId,
    int ClipStageIndex,
    int ClipStageRawIndex,
    StageInputKind Input,
    bool IsPassthrough,
    IArchitectureStagePayload ArchitecturePayload,
    StageOutputPlan Output)
{
    /// <summary>Resolved independently for every authored active stage.</summary>
    public ResolvedVideoModel ResolvedModel { get; init; }
}

internal enum StageInputKind
{
    RootMedia,
    EmptyLatent,
    SourceVideo,
    PreviousStage,
}


internal enum IntermediateOutputPolicy
{
    NotEligible,
    ControlledByHostSetting,
}

internal sealed record StageOutputPlan(
    bool IsTimelineTerminal,
    IntermediateOutputPolicy IntermediatePolicy,
    bool PreserveConfiguredAudioTrackSave);

/// <summary>A normalized outgoing boundary from clip N to clip N + 1.</summary>
internal sealed record BoundaryPlan(
    int FromClipId,
    BoundaryExecutionMode Requested,
    BoundaryExecutionMode Effective,
    int OverlapFrames,
    int ContinuityWindowFrames,
    bool RequiresRuntimeMergeValidation,
    BoundaryFallback Fallback)
{
    public int FrameStep { get; init; } = 1;

    public int MinFrames { get; init; } = 1;

    /// <summary>
    /// The next generated clip receives this boundary's outgoing audio tail as preserved opening
    /// context. This is generation-time conditioning, not final timeline mixing.
    /// </summary>
    public bool CarryAudio { get; init; }
}

internal enum BoundaryExecutionMode
{
    Cut,
    Continue,
    Crossfade,
}

internal enum BoundaryFallback
{
    None,
    TargetIsSourcedVideo,
    TargetHasNoStage,
    TargetHasFirstFrameReference,
    UnknownBoundaryKind,
    InsufficientFrameBudget,
    ArchitectureRuleUnsupported,
}
