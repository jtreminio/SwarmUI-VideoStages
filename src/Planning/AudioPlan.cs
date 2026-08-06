using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Planning;

/// <summary>
/// Selects the only source allowed to determine clip duration. ControlNet wins when both authored
/// duration flags are set.
/// </summary>
internal enum AudioLengthOwner
{
    Timeline,
    Audio,
    ControlNet
}

internal sealed record AudioMediaIdentityPlan(
    string Data,
    string FileName)
{
    internal static AudioMediaIdentityPlan From(UploadedMediaSpec media) => media is null
        ? null
        : new(media.Data, media.FileName);
}

internal sealed record AudioBaseSourcePlan(
    AudioSourceKind Kind,
    string RawSource,
    int? AceStepFunTrack,
    bool HasConfiguredTrack,
    AudioMediaIdentityPlan UploadedMedia);

internal sealed record AudioLengthPlan(AudioLengthOwner Owner);

internal sealed record AudioSegmentItemPlan(
    AudioSourceKind SourceKind,
    int? AceStepFunTrack,
    double StartSeconds,
    double TrimStartSeconds,
    double LengthSeconds,
    AudioMediaIdentityPlan UploadedMedia,
    double Volume = 1);

/// <summary>
/// The declared segments are represented as simple values rather than graph paths. A later executor
/// resolves uploads/tracks, reports unavailable runtime sources, and uses these windows unchanged.
/// </summary>
internal sealed record AudioSegmentPlan(ImmutableArray<AudioSegmentItemPlan> Items);

/// <summary>Output from one independently testable portion of audio planning.</summary>
internal sealed record AudioPlanComponentResult<TPlan>(
    TPlan Plan,
    ImmutableArray<PlanDiagnostic> Diagnostics);

/// <summary>
/// Pure projection of one <see cref="ClipSpec"/>'s audio policy. This contains no graph paths and
/// makes every audio ownership decision before workflow construction begins.
/// </summary>
internal sealed record AudioPlan(
    AudioBaseSourcePlan Base,
    AudioLengthPlan Length,
    AudioSegmentPlan Segments,
    ImmutableArray<PlanDiagnostic> Diagnostics);
