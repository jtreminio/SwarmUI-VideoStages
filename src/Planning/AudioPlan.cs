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

internal sealed record AudioLengthPlan(
    AudioLengthOwner Owner,
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
internal sealed record AudioSegmentPlan(ImmutableArray<AudioSegmentItemPlan> Items);

/// <summary>
/// Pure projection of one <see cref="ClipSpec"/>'s audio policy. This contains no graph paths and
/// makes every audio ownership decision before workflow construction begins.
/// </summary>
internal sealed record AudioPlan(
    AudioBaseSourcePlan Base,
    AudioLengthPlan Length,
    AudioSegmentPlan Segments,
    ImmutableArray<AudioPlanDiagnostic> Diagnostics);
