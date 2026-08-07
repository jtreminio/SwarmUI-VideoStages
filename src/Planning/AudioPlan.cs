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
/// One <see cref="ClipSpec"/>'s compiled audio policy; every ownership decision is final here.
/// A later executor resolves the span sources, reports the ones it cannot load, and uses the
/// windows unchanged.
/// </summary>
internal sealed record AudioPlan(
    AudioBaseSourcePlan Base,
    AudioLengthOwner LengthOwner,
    ImmutableArray<AudioSpanPlan> Spans,
    ImmutableArray<PlanDiagnostic> Diagnostics);
