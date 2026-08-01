using System.Collections.Immutable;

namespace VideoStages.Planning;

internal sealed record AudioTimelineTrackProjectionResult(
    ImmutableArray<AudioTimelineTrackPlan> Tracks,
    ImmutableArray<PlanDiagnostic> Diagnostics);

/// <summary>
/// Projects audio spans onto final clip windows and reports unresolved timing inputs.
/// </summary>
internal static class AudioTimelineTrackSpanProjector
{
    internal static AudioTimelineTrackProjectionResult Project(
        ImmutableArray<AudioTrackSpec> tracks,
        ImmutableArray<AudioTimelineClipWindow> clipWindows,
        IReadOnlyDictionary<int, int> clipIndices)
    {
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        HashSet<string> seenTrackIds = new(StringComparer.Ordinal);
        ImmutableArray<AudioTimelineTrackPlan>.Builder projectedTracks =
            ImmutableArray.CreateBuilder<AudioTimelineTrackPlan>();
        foreach (AudioTrackSpec track in tracks)
        {
            string trackId = track?.TrackId?.Trim() ?? "";
            AudioTrackValidation validation = AudioTimelineValidationPlanner.ValidateTrack(
                track,
                trackId,
                isDuplicateId: trackId.Length > 0 && !seenTrackIds.Add(trackId));
            diagnostics.AddRange(validation.Diagnostics);
            if (!validation.CanProject)
            {
                continue;
            }

            ImmutableArray<AudioTrackClipWindow>.Builder windows =
                ImmutableArray.CreateBuilder<AudioTrackClipWindow>();
            ImmutableArray<AudioTrackSpanSpec> spans = track.Spans.IsDefault ? [] : track.Spans;
            for (int spanIndex = 0; spanIndex < spans.Length; spanIndex++)
            {
                ProjectSpan(trackId, spanIndex, spans[spanIndex], clipWindows, clipIndices, diagnostics, windows);
            }
            projectedTracks.Add(new(
                trackId,
                track.Source,
                windows.ToImmutable(),
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
        ImmutableArray<PlanDiagnostic>.Builder diagnostics,
        ImmutableArray<AudioTrackClipWindow>.Builder destination)
    {
        if (AudioTimelineValidationPlanner.ValidateSpan(span, trackId, spanIndex)
            is PlanDiagnostic invalid)
        {
            diagnostics.Add(invalid);
            return;
        }

        int firstIndex = 0;
        int lastIndex = clipWindows.Length - 1;
        if (span.HasClipRange)
        {
            if (span.FirstClipId.HasValue && !clipIndices.TryGetValue(span.FirstClipId.Value, out firstIndex))
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning,
                    "audio.timeline.span.unknown_first_clip", $"Audio span starts at unknown clip {span.FirstClipId.Value}.",
                    TrackId: trackId, SpanIndex: spanIndex, ClipId: span.FirstClipId));
                return;
            }
            if (span.LastClipId.HasValue && !clipIndices.TryGetValue(span.LastClipId.Value, out lastIndex))
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning,
                    "audio.timeline.span.unknown_last_clip", $"Audio span ends at unknown clip {span.LastClipId.Value}.",
                    TrackId: trackId, SpanIndex: spanIndex, ClipId: span.LastClipId));
                return;
            }
            if (firstIndex > lastIndex)
            {
                diagnostics.Add(new(PlanDiagnosticSeverity.Warning,
                    "audio.timeline.span.reversed_clip_range", "An audio span's first clip follows its last clip.",
                    TrackId: trackId, SpanIndex: spanIndex));
                return;
            }
        }

        double? requestedStart = span.TimelineStartSeconds;
        double? requestedEnd = requestedStart + span.TimelineLengthSeconds;
        if (span.HasClipRelativeWindow)
        {
            AudioTimelineClipWindow owner = clipWindows[firstIndex];
            if (!owner.IsResolved)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.span.unresolved_clip_relative_timing",
                    "A clip-relative audio span is pending until its clip timing is known.",
                    TrackId: trackId,
                    SpanIndex: spanIndex,
                    ClipId: owner.ClipId));
                return;
            }
            requestedStart = owner.TimelineTimeAt(span.ClipStartOffsetSeconds.Value);
            requestedEnd = requestedStart + span.ClipLengthSeconds.Value;
        }
        double? sourceAnchor = requestedStart;
        List<AudioTrackClipWindow> projected = [];
        bool emitted = false;
        bool hadUnresolvedTiming = false;
        for (int clipIndex = firstIndex; clipIndex <= lastIndex; clipIndex++)
        {
            AudioTimelineClipWindow clip = clipWindows[clipIndex];
            if (!clip.IsResolved)
            {
                hadUnresolvedTiming = true;
                continue;
            }
            double clipStart = clip.TimelineStartSeconds.Value;
            double clipEnd = clipStart + clip.DurationSeconds.Value;
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
                span.SourceStartSeconds + (start - sourceAnchor.Value)));
            emitted = true;
        }
        if (hadUnresolvedTiming)
        {
            diagnostics.Add(new(PlanDiagnosticSeverity.Warning,
                "audio.timeline.span.unresolved_clip_timing",
                "An audio span crosses clip timing that is not yet known.",
                TrackId: trackId, SpanIndex: spanIndex));
            return;
        }
        destination.AddRange(projected);
        if (!emitted)
        {
            diagnostics.Add(new(PlanDiagnosticSeverity.Warning,
                "audio.timeline.span.empty_after_projection",
                "An audio span does not overlap any resolved final-timeline clip window.",
                TrackId: trackId, SpanIndex: spanIndex));
        }
    }
}
