using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Source identity for a timeline audio track. Upload and AceStepFun resolution occurs during
/// graph execution.
/// </summary>
internal sealed record AudioTimelineTrackSource(
    AudioSourceKind Kind,
    string Reference,
    AudioMediaIdentityPlan UploadedMedia = null);

/// <summary>
/// A track interval bounded by inclusive clip ids, timeline seconds, or their intersection.
/// </summary>
internal sealed record AudioTrackSpanSpec(
    int? FirstClipId = null,
    int? LastClipId = null,
    double? TimelineStartSeconds = null,
    double? TimelineLengthSeconds = null,
    double SourceStartSeconds = 0)
{
    public bool HasClipRange => FirstClipId.HasValue || LastClipId.HasValue;

    public bool HasTimelineWindow => TimelineStartSeconds.HasValue || TimelineLengthSeconds.HasValue;
}

/// <summary>
/// A track may have discontiguous spans. Overlapping tracks remain separate for runtime mixing.
/// </summary>
internal sealed record AudioTrackSpec(
    string TrackId,
    AudioTimelineTrackSource Source,
    ImmutableArray<AudioTrackSpanSpec> Spans,
    double Volume = 1);

/// <summary>
/// Maps a final clip interval between clip-relative and timeline seconds.
/// </summary>
internal sealed record AudioTimelineClipWindow(
    int ClipId,
    double? TimelineStartSeconds,
    double? DurationSeconds)
{
    internal bool IsResolved => TimelineStartSeconds.HasValue && DurationSeconds.HasValue;

    internal double TimelineTimeAt(double clipOffsetSeconds) =>
        TimelineStartSeconds.Value + Math.Clamp(clipOffsetSeconds, 0, DurationSeconds.Value);

    internal double ClipOffsetAt(double timelineSeconds) =>
        timelineSeconds - TimelineStartSeconds.Value;
}

/// <summary>
/// A track span projected onto one final clip. Source time follows the trimmed timeline across
/// continued and crossfaded seams.
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

/// <summary>Projected audio tracks over final clip timing.</summary>
internal sealed record AudioTimelinePlan(
    ImmutableArray<AudioTimelineClipWindow> ClipWindows,
    ImmutableArray<AudioTimelineTrackPlan> Tracks,
    ImmutableArray<PlanDiagnostic> Diagnostics)
{
    public static AudioTimelinePlan Empty { get; } = new([], [], []);
}
