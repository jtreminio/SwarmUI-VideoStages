using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>The verdict on one authored track: whether it can be projected, and why not.</summary>
internal sealed record AudioTrackValidation(
    bool CanProject,
    ImmutableArray<PlanDiagnostic> Diagnostics);

/// <summary>
/// The one owner of timeline audio validation. It rejects malformed authored tracks and spans
/// before projection, and checks projected windows for non-partitioning spans and cross-track
/// overlap afterwards, so the projector performs arithmetic only.
/// </summary>
internal static class AudioTimelineValidationPlanner
{
    internal static AudioTrackValidation ValidateTrack(
        AudioTrackSpec track,
        string trackId,
        bool isDuplicateId)
    {
        if (track is null)
        {
            return Rejected(new(
                PlanDiagnosticSeverity.Error,
                "audio.timeline.track.null",
                "A null timeline audio track was ignored."));
        }
        if (trackId.Length == 0)
        {
            return Rejected(new(
                PlanDiagnosticSeverity.Error,
                "audio.timeline.track.missing_id",
                "A timeline audio track needs a non-empty id."));
        }
        if (isDuplicateId)
        {
            return Rejected(new(
                PlanDiagnosticSeverity.Error,
                "audio.timeline.track.duplicate_id",
                $"Timeline audio track '{trackId}' is duplicated.",
                TrackId: trackId));
        }

        // A missing source and an empty span list are both reported, but neither stops projection:
        // the track still describes real timeline time that later validation must see.
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        if (track.Source is null)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Error,
                "audio.timeline.track.missing_source",
                $"Timeline audio track '{trackId}' has no source.",
                TrackId: trackId));
        }
        if ((track.Spans.IsDefault ? [] : track.Spans).IsEmpty)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.track.no_spans",
                $"Timeline audio track '{trackId}' has no spans.",
                TrackId: trackId));
        }
        return new(CanProject: true, diagnostics.ToImmutable());
    }

    /// <summary>
    /// Validates one authored span's shape. A non-null result means the span cannot be projected.
    /// </summary>
    internal static PlanDiagnostic ValidateSpan(
        AudioTrackSpanSpec span,
        string trackId,
        int spanIndex)
    {
        PlanDiagnostic Error(string code, string message) => new(
            PlanDiagnosticSeverity.Error,
            code,
            message,
            TrackId: trackId,
            SpanIndex: spanIndex);

        if (span is null)
        {
            return Error("audio.timeline.span.null", "A null audio span was ignored.");
        }
        if (!span.HasClipRange && !span.HasTimelineWindow && !span.HasClipRelativeWindow)
        {
            return Error(
                "audio.timeline.span.missing_owner",
                "An audio span needs a clip range, a timeline window, or both.");
        }
        if (!IsFiniteNonNegative(span.SourceStartSeconds))
        {
            return Error(
                "audio.timeline.span.invalid_source_start",
                "An audio span has an invalid source start.");
        }
        if (span.HasTimelineWindow
            && (!span.TimelineStartSeconds.HasValue || !span.TimelineLengthSeconds.HasValue
                || !IsFiniteNonNegative(span.TimelineStartSeconds.Value)
                || !IsFinitePositive(span.TimelineLengthSeconds.Value)))
        {
            return Error(
                "audio.timeline.span.invalid_timeline_window",
                "A timeline-owned audio span needs a finite non-negative start and positive length.");
        }
        if (span.HasClipRelativeWindow
            && (!span.ClipStartOffsetSeconds.HasValue || !span.ClipLengthSeconds.HasValue
                || !IsFiniteNonNegative(span.ClipStartOffsetSeconds.Value)
                || !IsFinitePositive(span.ClipLengthSeconds.Value)))
        {
            return Error(
                "audio.timeline.span.invalid_clip_relative_window",
                "A clip-relative audio span needs a finite non-negative offset and positive length.");
        }
        if (span.HasTimelineWindow && span.HasClipRelativeWindow)
        {
            return Error(
                "audio.timeline.span.conflicting_time_owners",
                "An audio span cannot use timeline-relative and clip-relative timing together.");
        }
        if (span.HasClipRelativeWindow
            && !(span.FirstClipId.HasValue
                && span.LastClipId.HasValue
                && span.FirstClipId == span.LastClipId))
        {
            return Error(
                "audio.timeline.span.clip_relative_requires_one_clip",
                "A clip-relative audio span must identify exactly one clip.");
        }
        return null;
    }

    /// <summary>Checks the projected windows for partitioning and cross-track overlap.</summary>
    internal static ImmutableArray<PlanDiagnostic> Validate(
        ImmutableArray<AudioTimelineTrackPlan> tracks)
    {
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        ValidateSpanPartitions(tracks, diagnostics);
        AddOverlapDiagnostics(tracks, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static AudioTrackValidation Rejected(PlanDiagnostic diagnostic) =>
        new(CanProject: false, [diagnostic]);

    private static void AddOverlapDiagnostics(
        ImmutableArray<AudioTimelineTrackPlan> tracks,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        AudioTrackClipWindow[] windows = tracks
            .SelectMany(track => track.Windows)
            .OrderBy(window => window.ClipId)
            .ThenBy(window => window.TimelineStartSeconds)
            .ThenBy(window => window.TrackId, StringComparer.Ordinal)
            .ToArray();
        HashSet<(string First, string Second, int ClipId)> reported = [];
        for (int i = 0; i < windows.Length; i++)
        {
            for (int j = i + 1; j < windows.Length && windows[j].ClipId == windows[i].ClipId; j++)
            {
                if (windows[i].TrackId == windows[j].TrackId
                    || windows[j].TimelineStartSeconds >= windows[i].TimelineStartSeconds + windows[i].DurationSeconds)
                {
                    continue;
                }
                (string first, string second) = string.CompareOrdinal(windows[i].TrackId, windows[j].TrackId) <= 0
                    ? (windows[i].TrackId, windows[j].TrackId)
                    : (windows[j].TrackId, windows[i].TrackId);
                if (reported.Add((first, second, windows[i].ClipId)))
                {
                    diagnostics.Add(new(PlanDiagnosticSeverity.Info,
                        "audio.timeline.overlapping_tracks",
                        $"Timeline tracks '{first}' and '{second}' overlap in clip {windows[i].ClipId}; execution must mix them.",
                        TrackId: first,
                        ClipId: windows[i].ClipId));
                }
            }
        }
    }

    private static void ValidateSpanPartitions(
        ImmutableArray<AudioTimelineTrackPlan> tracks,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        foreach (IGrouping<(string TrackId, int SpanIndex), AudioTrackClipWindow> group in tracks
            .SelectMany(track => track.Windows)
            .GroupBy(window => (window.TrackId, window.SpanIndex)))
        {
            AudioTrackClipWindow[] windows = group.OrderBy(window => window.TimelineStartSeconds).ToArray();
            for (int i = 1; i < windows.Length; i++)
            {
                AudioTrackClipWindow previous = windows[i - 1];
                AudioTrackClipWindow current = windows[i];
                bool overlaps = current.TimelineStartSeconds < previous.TimelineStartSeconds + previous.DurationSeconds;
                bool sourceSkipsOrRepeats = Math.Abs(current.SourceStartSeconds
                    - (previous.SourceStartSeconds + previous.DurationSeconds)) > 1e-9;
                if (overlaps || sourceSkipsOrRepeats)
                {
                    diagnostics.Add(new(PlanDiagnosticSeverity.Error,
                        "audio.timeline.span.non_partitioning_projection",
                        "A projected track span would double-consume or skip final timeline time.",
                        TrackId: group.Key.TrackId,
                        SpanIndex: group.Key.SpanIndex));
                    break;
                }
            }
        }
    }

    private static bool IsFiniteNonNegative(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    private static bool IsFinitePositive(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
}
