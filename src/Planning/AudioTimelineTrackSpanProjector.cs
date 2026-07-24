using System.Collections.Immutable;

namespace VideoStages.Planning;

internal sealed record AudioTimelineTrackProjectionResult(
    ImmutableArray<AudioTimelineTrackPlan> Tracks,
    ImmutableArray<AudioTimelineDiagnostic> Diagnostics);

/// <summary>Projects each authored span, including unresolved atomic pending spans, onto clip windows.</summary>
internal static class AudioTimelineTrackSpanProjector
{
    internal static AudioTimelineTrackProjectionResult Project(
        ImmutableArray<AudioTrackSpec> tracks,
        ImmutableArray<AudioTimelineClipWindow> clipWindows,
        IReadOnlyDictionary<int, int> clipIndices)
    {
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<AudioTimelineDiagnostic>();
        HashSet<string> seenTrackIds = new(StringComparer.Ordinal);
        ImmutableArray<AudioTimelineTrackPlan>.Builder projectedTracks =
            ImmutableArray.CreateBuilder<AudioTimelineTrackPlan>();
        foreach (AudioTrackSpec track in tracks)
        {
            if (track is null)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.track.null", "A null timeline audio track was ignored."));
                continue;
            }
            string trackId = track.TrackId?.Trim() ?? "";
            if (trackId.Length == 0)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.track.missing_id", "A timeline audio track needs a non-empty id."));
                continue;
            }
            if (!seenTrackIds.Add(trackId))
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.track.duplicate_id", $"Timeline audio track '{trackId}' is duplicated.", trackId));
                continue;
            }
            if (track.Source is null)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.track.missing_source", $"Timeline audio track '{trackId}' has no source.", trackId));
            }

            ImmutableArray<AudioTrackClipWindow>.Builder windows =
                ImmutableArray.CreateBuilder<AudioTrackClipWindow>();
            ImmutableArray<PendingAudioTrackSpan>.Builder pending =
                ImmutableArray.CreateBuilder<PendingAudioTrackSpan>();
            ImmutableArray<AudioTrackSpanSpec> spans = track.Spans.IsDefault ? [] : track.Spans;
            if (spans.IsEmpty)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
                    "audio.timeline.track.no_spans", $"Timeline audio track '{trackId}' has no spans.", trackId));
            }
            for (int spanIndex = 0; spanIndex < spans.Length; spanIndex++)
            {
                ProjectSpan(trackId, spanIndex, spans[spanIndex], clipWindows, clipIndices, diagnostics, windows, pending);
            }
            projectedTracks.Add(new(
                trackId,
                track.Source,
                spans,
                windows.ToImmutable(),
                pending.ToImmutable(),
                track.Volume));
        }
        return new(projectedTracks.ToImmutable(), diagnostics.ToImmutable());
    }

    private static void ProjectSpan(
        string trackId,
        int spanIndex,
        AudioTrackSpanSpec span,
        ImmutableArray<AudioTimelineClipWindow> clipWindows,
        IReadOnlyDictionary<int, int> clipIndices,
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics,
        ImmutableArray<AudioTrackClipWindow>.Builder destination,
        ImmutableArray<PendingAudioTrackSpan>.Builder pending)
    {
        if (span is null)
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.span.null", "A null audio span was ignored.", trackId, spanIndex));
            return;
        }
        if (!span.HasClipRange && !span.HasTimelineWindow && !span.HasClipRelativeWindow)
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.span.missing_owner",
                "An audio span needs a clip range, a timeline window, or both.", trackId, spanIndex));
            return;
        }
        if (!IsFiniteNonNegative(span.SourceStartSeconds))
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.span.invalid_source_start", "An audio span has an invalid source start.", trackId, spanIndex));
            return;
        }
        if (span.HasTimelineWindow
            && (!span.TimelineStartSeconds.HasValue || !span.TimelineLengthSeconds.HasValue
                || !IsFiniteNonNegative(span.TimelineStartSeconds.Value)
                || !IsFinitePositive(span.TimelineLengthSeconds.Value)))
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.span.invalid_timeline_window",
                "A timeline-owned audio span needs a finite non-negative start and positive length.", trackId, spanIndex));
            return;
        }
        if (span.HasClipRelativeWindow
            && (!span.ClipStartOffsetSeconds.HasValue || !span.ClipLengthSeconds.HasValue
                || !IsFiniteNonNegative(span.ClipStartOffsetSeconds.Value)
                || !IsFinitePositive(span.ClipLengthSeconds.Value)))
        {
            diagnostics.Add(new(
                AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.span.invalid_clip_relative_window",
                "A clip-relative audio span needs a finite non-negative offset and positive length.",
                trackId,
                spanIndex));
            return;
        }
        if (span.HasTimelineWindow && span.HasClipRelativeWindow)
        {
            diagnostics.Add(new(
                AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.span.conflicting_time_owners",
                "An audio span cannot use timeline-relative and clip-relative timing together.",
                trackId,
                spanIndex));
            return;
        }

        int firstIndex = 0;
        int lastIndex = clipWindows.Length - 1;
        if (span.HasClipRange)
        {
            if (span.FirstClipId.HasValue && !clipIndices.TryGetValue(span.FirstClipId.Value, out firstIndex))
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.span.unknown_first_clip", $"Audio span starts at unknown clip {span.FirstClipId.Value}.",
                    trackId, spanIndex, span.FirstClipId));
                return;
            }
            if (span.LastClipId.HasValue && !clipIndices.TryGetValue(span.LastClipId.Value, out lastIndex))
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.span.unknown_last_clip", $"Audio span ends at unknown clip {span.LastClipId.Value}.",
                    trackId, spanIndex, span.LastClipId));
                return;
            }
            if (firstIndex > lastIndex)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.span.reversed_clip_range", "An audio span's first clip follows its last clip.", trackId, spanIndex));
                return;
            }
        }

        double? requestedStart = span.TimelineStartSeconds;
        double? requestedEnd = requestedStart + span.TimelineLengthSeconds;
        if (span.HasClipRelativeWindow)
        {
            bool identifiesOneClip = span.FirstClipId.HasValue
                && span.LastClipId.HasValue
                && span.FirstClipId == span.LastClipId;
            if (!identifiesOneClip)
            {
                diagnostics.Add(new(
                    AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.span.clip_relative_requires_one_clip",
                    "A clip-relative audio span must identify exactly one clip.",
                    trackId,
                    spanIndex));
                return;
            }
            AudioTimelineClipWindow owner = clipWindows[firstIndex];
            if (owner.TimelineStartSeconds is not double ownerStart)
            {
                const string reason = "audio.timeline.span.unresolved_clip_relative_timing";
                pending.Add(new(spanIndex, span, reason));
                diagnostics.Add(new(
                    AudioTimelineDiagnosticSeverity.Warning,
                    reason,
                    "A clip-relative audio span is pending until its clip timing is known.",
                    trackId,
                    spanIndex,
                    owner.ClipId));
                return;
            }
            requestedStart = ownerStart + span.ClipStartOffsetSeconds.Value;
            requestedEnd = requestedStart + span.ClipLengthSeconds.Value;
        }
        double? sourceAnchor = requestedStart;
        AudioTimelineSpanOwnership ownership = span.HasClipRelativeWindow
            ? AudioTimelineSpanOwnership.ClipRelativeWindow
            : span.HasClipRange
            ? span.HasTimelineWindow
                ? AudioTimelineSpanOwnership.ClipRangeAndTimelineWindow
                : AudioTimelineSpanOwnership.ClipRange
            : AudioTimelineSpanOwnership.TimelineWindow;
        List<AudioTrackClipWindow> projected = [];
        bool emitted = false;
        bool hadUnresolvedTiming = false;
        for (int clipIndex = firstIndex; clipIndex <= lastIndex; clipIndex++)
        {
            AudioTimelineClipWindow clip = clipWindows[clipIndex];
            if (clip.TimelineStartSeconds is not double clipStart || clip.DurationSeconds is not double clipDuration)
            {
                hadUnresolvedTiming = true;
                continue;
            }
            double clipEnd = clipStart + clipDuration;
            double start = requestedStart.HasValue ? Math.Max(clipStart, requestedStart.Value) : clipStart;
            double end = requestedEnd.HasValue ? Math.Min(clipEnd, requestedEnd.Value) : clipEnd;
            if (end <= start)
            {
                continue;
            }
            sourceAnchor ??= clipStart;
            projected.Add(new(
                trackId,
                spanIndex,
                clip.ClipId,
                start,
                end - start,
                span.SourceStartSeconds + (start - sourceAnchor.Value),
                ownership,
                clip.IsProvisional));
            emitted = true;
        }
        if (hadUnresolvedTiming)
        {
            const string reason = "audio.timeline.span.unresolved_clip_timing";
            pending.Add(new(spanIndex, span, reason));
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
                reason,
                "An audio span crosses clip timing that is not yet known.", trackId, spanIndex));
            return;
        }
        destination.AddRange(projected);
        if (!emitted)
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
                "audio.timeline.span.empty_after_projection",
                "An audio span does not overlap any resolved final-timeline clip window.", trackId, spanIndex));
        }
    }

    private static bool IsFiniteNonNegative(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    private static bool IsFinitePositive(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
}
