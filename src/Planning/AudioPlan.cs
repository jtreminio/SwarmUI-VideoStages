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

internal sealed record AudioBaseSourcePlan(
    AudioSourceKind Kind,
    string RawSource,
    int? AceStepFunTrack,
    bool HasConfiguredTrack,
    UploadedMediaSpec UploadedMedia);

internal sealed record AudioSpanPlan(
    AudioSourceKind SourceKind,
    int? AceStepFunTrack,
    double StartSeconds,
    double TrimStartSeconds,
    double LengthSeconds,
    UploadedMediaSpec UploadedMedia,
    double Volume = 1);

/// <summary>
/// A later executor resolves uploads/tracks, reports unavailable runtime sources, and uses these
/// windows unchanged.
/// </summary>
/// <summary>
/// Pure projection of one <see cref="ClipSpec"/>'s audio policy. This contains no graph paths and
/// makes every audio ownership decision before workflow construction begins.
/// </summary>
internal sealed record AudioPlan(
    AudioBaseSourcePlan Base,
    AudioLengthOwner LengthOwner,
    ImmutableArray<AudioSpanPlan> Spans,
    ImmutableArray<PlanDiagnostic> Diagnostics);
