using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// The configured source of a clip's lockable audio track. Voice-reference samples deliberately
/// live outside this enum: they condition generated speech and are never a locked track.
/// </summary>
internal enum AudioBaseSourceKind
{
    None,
    Native,
    Upload,
    AceStepFun,
    ControlNet
}

internal enum AudioVoiceReferenceKind
{
    None,
    ClipUpload,
    IcLoraDriveVideo
}

/// <summary>
/// The single configuration source allowed to determine clip duration. ControlNet wins when both
/// duration flags are present, matching the parser and preventing two frame-count nodes from racing.
/// </summary>
internal enum AudioLengthOwner
{
    Timeline,
    Audio,
    ControlNet
}

internal enum AudioSegmentMode
{
    None,
    MixOverBase,
    PreserveWindowedNoBase
}

/// <summary>
/// Whether the segment executor must wait for artifact resolution before it can commit to the
/// preferred segment mode. Native, AceStepFun and ControlNet tracks are configured here but can
/// still be absent from a particular host workflow.
/// </summary>
internal enum AudioSegmentBaseResolutionRequirement
{
    NotRequired,
    NoBaseConfigured,
    ResolveAtExecution
}

internal enum AudioSegmentSourceKind
{
    Upload,
    AceStepFun
}

/// <summary>
/// Stable compiler diagnostic. <see cref="Code"/> is intentionally machine-friendly for plan
/// snapshots and tests; <see cref="Message"/> is a concise explanation suitable for a UI later.
/// </summary>
internal sealed record AudioPlanDiagnostic(string Code, string Message);

internal sealed record AudioMediaIdentityPlan(
    string Data,
    string FileName);

internal sealed record AudioBaseSourcePlan(
    AudioBaseSourceKind Kind,
    string RawSource,
    int? AceStepFunTrack,
    bool HasConfiguredTrack,
    AudioMediaIdentityPlan UploadedMedia);

/// <summary>
/// A speaker-identity input for LTX audio tokens. It is independent from <see cref="AudioBaseSourcePlan"/>
/// so a drive-video voice reference can coexist with a native/uploaded locked track.
/// </summary>
internal sealed record AudioVoiceReferencePlan(
    AudioVoiceReferenceKind Kind,
    bool IsRequested,
    bool HasConfiguredSample,
    AudioMediaIdentityPlan Media,
    int? IcLoraEntryIndex);

internal sealed record AudioLengthPlan(
    AudioLengthOwner Owner,
    bool AudioWasRequested,
    bool ControlNetWasRequested,
    bool NonHandoffInjectionMatchesAudioLength,
    bool RootHandoffInjectionMatchesAudioLength);

internal sealed record AudioSegmentItemPlan(
    AudioSegmentSourceKind SourceKind,
    int? AceStepFunTrack,
    double StartSeconds,
    double TrimStartSeconds,
    double LengthSeconds,
    AudioMediaIdentityPlan UploadedMedia);

/// <summary>
/// The declared segments are represented as simple values rather than graph paths. A later executor
/// resolves uploads/tracks, reports unavailable runtime sources, and uses these windows unchanged.
/// </summary>
internal sealed record AudioSegmentPlan(
    AudioSegmentMode Mode,
    AudioSegmentBaseResolutionRequirement BaseResolutionRequirement,
    ImmutableArray<AudioSegmentItemPlan> Items);

internal sealed record AudioReusePlan(
    bool IsRequested,
    bool IsEligible,
    int CaptureStageIndex,
    int ReuseFromStageIndex);

/// <summary>
/// Pure projection of one <see cref="ClipSpec"/>'s audio policy. This contains no graph paths and
/// makes every audio ownership decision before workflow construction begins.
/// </summary>
internal sealed record AudioPlan(
    AudioBaseSourcePlan Base,
    AudioVoiceReferencePlan VoiceReference,
    AudioLengthPlan Length,
    AudioSegmentPlan Segments,
    AudioReusePlan Reuse,
    ImmutableArray<AudioPlanDiagnostic> Diagnostics);
