using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Source identity for a timeline-wide audio track. This is deliberately metadata only: resolving
/// an upload or an AceStepFun decode remains a graph-execution concern.
/// </summary>
internal enum AudioTimelineTrackSourceKind
{
    Upload,
    AceStepFun,
    Native,
    ControlNet,
    External
}

internal sealed record AudioTimelineTrackSource(
    AudioTimelineTrackSourceKind Kind,
    string Reference,
    AudioMediaIdentityPlan UploadedMedia = null);

/// <summary>
/// One requested interval of a track. A span may be bounded by clip ids, a timeline seconds window,
/// or both; when both are supplied their intersection is used. Clip endpoints are inclusive.
/// </summary>
internal sealed record AudioTrackSpanSpec(
    int? FirstClipId = null,
    int? LastClipId = null,
    double? TimelineStartSeconds = null,
    double? TimelineLengthSeconds = null,
    double SourceStartSeconds = 0,
    double? ClipStartOffsetSeconds = null,
    double? ClipLengthSeconds = null)
{
    public bool HasClipRange => FirstClipId.HasValue || LastClipId.HasValue;

    public bool HasTimelineWindow => TimelineStartSeconds.HasValue || TimelineLengthSeconds.HasValue;

    public bool HasClipRelativeWindow => ClipStartOffsetSeconds.HasValue || ClipLengthSeconds.HasValue;
}

/// <summary>
/// A logical track may have discontiguous spans. Multiple tracks may intentionally overlap; the
/// eventual runtime mixer receives their derived windows separately rather than flattening them.
/// </summary>
internal sealed record AudioTrackSpec(
    string TrackId,
    AudioTimelineTrackSource Source,
    ImmutableArray<AudioTrackSpanSpec> Spans,
    double Volume = 1);

internal enum AudioTimelineDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>Stable, graph-independent validation result for timeline audio authoring.</summary>
internal sealed record AudioTimelineDiagnostic(
    AudioTimelineDiagnosticSeverity Severity,
    string Code,
    string Message,
    string TrackId = null,
    int? SpanIndex = null,
    int? ClipId = null);

/// <summary>
/// One final timeline clip interval.
/// </summary>
internal sealed record AudioTimelineClipWindow(
    int ClipId,
    double? TimelineStartSeconds,
    double? DurationSeconds);

/// <summary>
/// A track/span projected onto one final clip. Source time advances with the final, trimmed timeline
/// rather than authored clip duration, so audio cannot drift after continued or crossfaded seams.
/// </summary>
internal sealed record AudioTrackClipWindow(
    string TrackId,
    int SpanIndex,
    int ClipId,
    double TimelineStartSeconds,
    double DurationSeconds,
    double SourceStartSeconds);

internal sealed record AudioTimelineTrackPlan(
    string TrackId,
    AudioTimelineTrackSource Source,
    ImmutableArray<AudioTrackClipWindow> Windows,
    double Volume = 1);

/// <summary>
/// Timeline-wide audio policy. The clip-local <see cref="AudioPlan"/> remains on every
/// <see cref="ClipPlan"/> for compatibility; this plan always projects today's clip-local base
/// tracks and segments, and can additionally carry authored cross-clip tracks.
/// </summary>
internal sealed record AudioTimelinePlan(
    ImmutableArray<AudioTimelineClipWindow> ClipWindows,
    ImmutableArray<AudioTimelineTrackPlan> Tracks,
    ImmutableArray<AudioTimelineDiagnostic> Diagnostics)
{
    public static AudioTimelinePlan Empty { get; } = new([], [], []);
}
