using System.Collections.Immutable;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;

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
}

/// <summary>Compiled host-root ownership decisions.</summary>
internal sealed record RootPlan(
    HostRootKind HostKind,
    bool DiscardsRoot,
    bool UsesGeneratedClipDonor,
    bool InterceptsHostCore,
    bool UsesStageHandoff,
    bool DropsTextToVideoRootDonor,
    bool DiscardsTextToVideoRoot)
{
    public bool ReplacesTextToVideoRootStage(StagePlan stage, ClipPlan clip) =>
        DiscardsTextToVideoRoot
        && clip.EntryMode == ArchitectureEntryMode.TextToVideo
        && stage.Input == StageInputKind.EmptyLatent
        && stage.ClipStageIndex == 0;
}

internal enum HostRootKind
{
    ImageToVideo,
    TextToVideoRoot,
}

/// <summary>
/// The narrow bit of host state required to plan root ownership. It holds only immutable facts;
/// runtime media and graph nodes remain outside the plan compiler.
/// </summary>
internal sealed record RootEnvironment(
    HostRootKind HostKind,
    bool CanHandoffHostCore = false)
{
    public static RootEnvironment FromSpec(TimelineSpec spec) => new(
        spec.IsTextToVideo ? HostRootKind.TextToVideoRoot : HostRootKind.ImageToVideo);
}

internal sealed record ClipPlan(
    int ClipId,
    int? Frames,
    ArchitectureEntryMode EntryMode,
    InitVideoPlan InitVideo,
    IReadOnlyList<StagePlan> Stages,
    AudioPlan Audio,
    bool SavesAudioTrack = false)
{
    /// <summary>The one architecture established for this clip before graph mutation begins.</summary>
    public VideoArchitectureDescriptor Architecture { get; init; }

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
    bool IsIntermediateStage)
{
    public ResolvedVideoModel ResolvedModel { get; init; }

    public StageCorePlan Core =>
        ArchitecturePayload?.Core
        ?? throw Invariant.Failure(
            $"Stage {StageId} has no common execution settings.");

    public bool ContinuesSamplingFromPreviousStage =>
        ArchitecturePayload?.ContinuesSamplingFromPreviousStage == true;
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


/// <summary>A normalized outgoing boundary from clip N to clip N + 1.</summary>
internal sealed record BoundaryPlan(
    int FromClipId,
    BoundaryJoinType Effective,
    int OverlapFrames,
    int ContinuityWindowFrames,
    BoundaryFallbackReason Fallback)
{
    public ContinueBoundaryMode ContinueMode { get; init; } = ContinueBoundaryMode.Overlap;

    public int FrameStep { get; init; } = 1;

    public int MinFrames { get; init; } = 1;

    /// <summary>
    /// Whether the next clip receives the outgoing audio tail as generation-time conditioning.
    /// </summary>
    public bool CarryAudio { get; init; }

    public double ReferenceScale { get; init; } = 1;

    public bool ReferenceIncludeSoundtrack { get; init; } = true;
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
    TargetHasDerivedDuration,
    InsufficientFrameBudget,
    ArchitectureRuleUnsupported,
}
