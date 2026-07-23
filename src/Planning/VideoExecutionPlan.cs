using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// An immutable, graph-independent description of the LTX timeline that VideoStages will run.
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
    IReadOnlyList<VideoPlanDiagnostic> Diagnostics)
{
    /// <summary>Whether the author explicitly configured both timeline dimensions.</summary>
    public bool HasConfiguredResolution { get; init; } = true;

    /// <summary>
    /// Clip-local audio projected onto the timeline. Call
    /// <see cref="AudioTimelinePlanCompiler"/> when a caller supplies timeline track spans; the per-clip <see cref="ClipPlan.Audio"/> remains
    /// the existing single-clip plan either way.
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
    AudioPlan Audio);

internal sealed record SourceVideoPlan(
    string Data,
    string FileName,
    double StartSeconds,
    int TargetWidth,
    int TargetHeight,
    int TargetFramesPerSecond);

/// <summary>
/// A complete, graph-free LTX stage instruction.
/// </summary>
internal sealed record StagePlan(
    int StageId,
    int ClipStageIndex,
    int ClipStageRawIndex,
    StageInputKind Input,
    bool IsPassthrough,
    StageCorePlan Core,
    GuideReferencePlan Guide,
    StageUpscalePlan Upscale,
    ImmutableArray<NormalLoraPlan> Loras,
    ImmutableArray<IcLoraPlan> IcLoras,
    RetakePlan Retake,
    PromptRelayPlan PromptRelay,
    ImmutableArray<ImageReferencePlan> FrameReferences,
    StageAudioAction AudioAction,
    StageOutputPlan Output)
{ }

internal sealed record StageCorePlan(
    string Model,
    double Control,
    int Steps,
    double CfgScale,
    string Sampler,
    string Scheduler,
    double? ControlNetStrength,
    bool ImageReferenceWasExplicit);

internal enum StageInputKind
{
    RootMedia,
    EmptyLatent,
    SourceVideo,
    PreviousStage,
}

internal enum StageUpscaleMode
{
    None,
    Pixel,
    Model,
    Latent,
    LatentModel,
    Unsupported,
}

internal sealed record StageUpscalePlan(
    StageUpscaleMode Mode,
    double Factor,
    string RawMethod,
    string MethodName);

internal enum GuideReferenceKind
{
    Base,
    Refiner,
    Generated,
    PreviousStage,
    ExplicitStage,
    Base2Edit,
    Unknown,
}

internal sealed record GuideReferencePlan(
    GuideReferenceKind Kind,
    string RawValue,
    int? ReferencedStageIndex);

internal sealed record NormalLoraPlan(
    string Name,
    double ModelWeight,
    double TextEncoderWeight);

internal enum IcLoraDriveSourceKind
{
    UploadedMedia,
    StageInput,
    SourcedClipInput,
    ControlNet,
    LoaderOnly,
    Unknown,
}

internal enum IcLoraControlMode
{
    None,
    Canny,
    Depth,
    Normal,
    Unknown,
}

internal enum IcLoraUploadedMediaKind
{
    None,
    Image,
    Video,
    Unknown,
}

internal sealed record IcLoraDrivePlan(
    IcLoraDriveSourceKind Kind,
    string RawSource,
    int? ControlNetIndex,
    IcLoraUploadedMediaKind UploadedMediaKind,
    string UploadedData,
    bool HasDriveMedia);

internal sealed record IcLoraPlan(
    int EntryIndex,
    string ModelName,
    bool UsesAutoModel,
    string Preset,
    double ModelStrength,
    double AttentionStrength,
    IcLoraControlMode ControlMode,
    IcLoraDrivePlan Drive,
    double? GuideStrength);

internal sealed record RetakePlan(
    int StartFrame,
    int LengthFrames,
    double Strength);

internal enum PromptRelayMode
{
    None,
    SinglePromptOverride,
    Relay,
    RequiresRuntimeLength,
}

internal sealed record PromptWindowPlan(
    string Prompt,
    double StartSeconds,
    double DurationSeconds,
    double EndSeconds);

internal sealed record PromptRelaySegmentPlan(
    string Prompt,
    double Seconds);

internal sealed record PromptRelayPlan(
    PromptRelayMode Mode,
    ImmutableArray<PromptWindowPlan> AuthoredWindows,
    ImmutableArray<PromptRelaySegmentPlan> Segments);

internal enum ImageReferenceSourceKind
{
    Upload,
    Base,
    Refiner,
    Base2Edit,
    Unknown,
}

internal enum ImageReferenceFrameOrigin
{
    Start,
    End,
}

internal sealed record ImageReferencePlan(
    int Index,
    ImageReferenceSourceKind SourceKind,
    string RawSource,
    int? Base2EditStageIndex,
    int Frame,
    ImageReferenceFrameOrigin FrameOrigin,
    double Strength,
    string UploadFileName,
    string InlineData);

internal enum StageAudioAction
{
    None,
    CaptureForReuse,
    ReuseCaptured,
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
    BoundaryExecutionMode Effective,
    int OverlapFrames,
    int ContinuityWindowFrames,
    bool RequiresRuntimeMergeValidation,
    BoundaryFallback Fallback);

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
}

internal sealed record VideoPlanDiagnostic(
    VideoPlanDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? ClipId = null,
    int? StageId = null);

internal enum VideoPlanDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
