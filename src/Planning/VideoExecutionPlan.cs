using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

/// <summary>
/// Immutable, graph-independent timeline plan. It excludes <c>WorkflowGenerator</c>, graph nodes,
/// and runtime media so compilation can run before graph construction.
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
    /// Projection of authored timeline tracks onto final clip windows. Per-clip base audio remains
    /// in <see cref="ClipPlan.Audio"/>.
    /// </summary>
    public AudioTimelinePlan AudioTimeline { get; init; } = AudioTimelinePlan.Empty;
}

/// <summary>What the timeline does with the host's pre-VideoStages result.</summary>
internal sealed record RootPlan(
    HostRootKind HostKind,
    RootUse Use,
    HostCoreDisposition CoreDisposition,
    NativeAudioDisposition NativeAudioDisposition);

internal enum HostRootKind
{
    ImageToVideo,
    TextToVideoRoot,
}

/// <summary>Who consumes the host root media, independently from what happens to host core nodes.</summary>
internal enum RootUse
{
    None,
    ClipZeroSeed,
    GeneratedClipDonor,
    Discard,
}

internal enum HostCoreDisposition
{
    Keep,
    Handoff,
    Drop,
}

internal enum NativeAudioDisposition
{
    KeepHostAudio,
    MakeAvailableToTimeline,
    DiscardWithRoot,
}

/// <summary>
/// The narrow bit of host state required to plan root ownership. It holds only immutable facts;
/// runtime media and graph nodes remain outside the plan compiler.
/// </summary>
internal sealed record RootEnvironment(
    HostRootKind HostKind,
    bool CanHandoffHostCore = false)
{
    public static RootEnvironment FromSpec(VideoStagesSpec spec) => new(
        spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo);
}

/// <summary>The primary artifact that starts a clip before its stages run.</summary>
internal enum ClipInputKind
{
    RootMedia,
    EmptyLatent,
    InitVideo,
}

internal sealed record ClipPlan(
    int ClipId,
    int? Frames,
    ClipInputKind Input,
    bool HasInitVideo,
    InitVideoPlan InitVideo,
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

internal sealed record InitVideoPlan(
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
    public ResolvedVideoModel ResolvedModel { get; init; }

    public StageCorePlan Core =>
        ArchitecturePayload?.Core
        ?? throw VideoStagesInvariant.Failure(
            $"Stage {StageId} has no common execution settings.");
}

/// <summary>Architecture-neutral settings shared by every generated stage.</summary>
internal sealed record StageCorePlan(
    double Control,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    StageUpscalePlan Upscale,
    ImmutableArray<NormalLoraPlan> Loras);

internal enum StageInputKind
{
    RootMedia,
    EmptyLatent,
    InitVideo,
    PreviousStage,
}


internal enum IntermediateOutputEligibility
{
    NotEligible,
    ControlledByHostSetting,
}

internal sealed record StageOutputPlan(
    bool IsTimelineTerminal,
    IntermediateOutputEligibility IntermediatePolicy,
    bool PreserveConfiguredAudioTrackSave);

/// <summary>A normalized outgoing boundary from clip N to clip N + 1.</summary>
internal sealed record BoundaryPlan(
    int FromClipId,
    BoundaryJoinType Requested,
    BoundaryJoinType Effective,
    int OverlapFrames,
    int ContinuityWindowFrames,
    bool RequiresRuntimeMergeValidation,
    BoundaryFallbackReason Fallback)
{
    public int FrameStep { get; init; } = 1;

    public int MinFrames { get; init; } = 1;

    /// <summary>
    /// Whether the next clip receives the outgoing audio tail as generation-time conditioning.
    /// </summary>
    public bool CarryAudio { get; init; }
}

internal enum BoundaryJoinType
{
    Cut,
    Continue,
    Crossfade,
}

internal enum BoundaryFallbackReason
{
    None,
    TargetHasInitVideo,
    TargetHasNoStage,
    TargetHasFirstFrameReference,
    InsufficientFrameBudget,
    ArchitectureRuleUnsupported,
}
