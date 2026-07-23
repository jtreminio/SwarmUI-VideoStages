using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Projects logical audio-track spans into final timeline windows. It shares the planner's boundary
/// timing instead of attempting to infer trims from workflow nodes, and performs no graph mutation.
/// </summary>
internal static class AudioTimelinePlanCompiler
{
    public static AudioTimelinePlan Compile(
        VideoExecutionPlan videoPlan,
        ImmutableArray<AudioTrackSpec> tracks = default)
    {
        ArgumentNullException.ThrowIfNull(videoPlan);

        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<AudioTimelineDiagnostic>();
        ImmutableArray<AudioTimelineClipWindow> clipWindows = BuildClipWindows(videoPlan, diagnostics);
        ImmutableArray<AudioTrackSpec> compatibilityTracks = BuildCompatibilityTrackSpecs(videoPlan, clipWindows);
        if (tracks.IsDefault)
        {
            tracks = [];
        }
        tracks = compatibilityTracks.AddRange(tracks);

        Dictionary<int, int> clipIndices = [];
        for (int i = 0; i < clipWindows.Length; i++)
        {
            if (!clipIndices.TryAdd(clipWindows[i].ClipId, i))
            {
                diagnostics.Add(new(
                    AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.clip.duplicate_id",
                    $"Timeline clip id {clipWindows[i].ClipId} is duplicated; only the first is addressable.",
                    ClipId: clipWindows[i].ClipId));
            }
        }
        HashSet<string> seenTrackIds = new(StringComparer.Ordinal);
        ImmutableArray<AudioTimelineTrackPlan>.Builder projectedTracks = ImmutableArray.CreateBuilder<AudioTimelineTrackPlan>();
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

            ImmutableArray<AudioTrackClipWindow>.Builder windows = ImmutableArray.CreateBuilder<AudioTrackClipWindow>();
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
                ProjectSpan(
                    trackId,
                    spanIndex,
                    spans[spanIndex],
                    clipWindows,
                    clipIndices,
                    diagnostics,
                    windows,
                    pending);
            }
            projectedTracks.Add(new(
                trackId,
                track.Source,
                spans,
                windows.ToImmutable(),
                pending.ToImmutable()));
        }

        ValidateSpanPartitions(projectedTracks, diagnostics);
        AddOverlapDiagnostics(projectedTracks, diagnostics);
        return new(clipWindows, projectedTracks.ToImmutable(), diagnostics.ToImmutable());
    }

    /// <summary>
    /// Existing clip-local audio becomes an explicit set of one-clip tracks. This makes the default
    /// projection useful immediately while leaving <see cref="AudioPlan"/> unchanged for callers
    /// that still execute the legacy single-clip path.
    /// </summary>
    private static ImmutableArray<AudioTrackSpec> BuildCompatibilityTrackSpecs(
        VideoExecutionPlan videoPlan,
        ImmutableArray<AudioTimelineClipWindow> clipWindows)
    {
        ImmutableArray<AudioTrackSpec>.Builder tracks = ImmutableArray.CreateBuilder<AudioTrackSpec>();
        for (int index = 0; index < videoPlan.Clips.Count; index++)
        {
            ClipPlan clip = videoPlan.Clips[index];
            if (clip.Audio.Base.Kind != AudioBaseSourceKind.None && clip.Audio.Base.HasConfiguredTrack)
            {
                tracks.Add(new(
                    $"clip-{clip.ClipId}-base",
                    new(
                        MapBaseSourceKind(clip.Audio.Base.Kind),
                        clip.Audio.Base.UploadedMedia?.FileName
                            ?? clip.Audio.Base.RawSource),
                    [new AudioTrackSpanSpec(FirstClipId: clip.ClipId, LastClipId: clip.ClipId)]));
            }

            for (int segmentIndex = 0; segmentIndex < clip.Audio.Segments.Items.Length; segmentIndex++)
            {
                AudioSegmentItemPlan segment = clip.Audio.Segments.Items[segmentIndex];
                tracks.Add(new(
                    $"clip-{clip.ClipId}-segment-{segmentIndex}",
                    new(
                        segment.SourceKind == AudioSegmentSourceKind.AceStepFun
                            ? AudioTimelineTrackSourceKind.AceStepFun
                            : AudioTimelineTrackSourceKind.Upload,
                        segment.SourceKind == AudioSegmentSourceKind.AceStepFun
                            ? $"audio{segment.AceStepFunTrack}"
                            : segment.UploadedMedia?.FileName
                                ?? $"clip-{clip.ClipId}-segment-{segmentIndex}"),
                    [new AudioTrackSpanSpec(
                        FirstClipId: clip.ClipId,
                        LastClipId: clip.ClipId,
                        SourceStartSeconds: segment.TrimStartSeconds,
                        ClipStartOffsetSeconds: segment.StartSeconds,
                        ClipLengthSeconds: segment.LengthSeconds)]));
            }
        }
        return tracks.ToImmutable();
    }

    private static AudioTimelineTrackSourceKind MapBaseSourceKind(AudioBaseSourceKind kind) => kind switch
    {
        AudioBaseSourceKind.Upload => AudioTimelineTrackSourceKind.Upload,
        AudioBaseSourceKind.AceStepFun => AudioTimelineTrackSourceKind.AceStepFun,
        AudioBaseSourceKind.ControlNet => AudioTimelineTrackSourceKind.ControlNet,
        AudioBaseSourceKind.Native => AudioTimelineTrackSourceKind.Native,
        _ => AudioTimelineTrackSourceKind.External,
    };

    private static ImmutableArray<AudioTimelineClipWindow> BuildClipWindows(
        VideoExecutionPlan videoPlan,
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics)
    {
        if (videoPlan.FramesPerSecond <= 0)
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.invalid_fps", "Timeline audio windows require a positive frames-per-second value."));
            return [.. videoPlan.Clips.Select(clip => new AudioTimelineClipWindow(
                clip.ClipId, null, null, null, 0, IsProvisional: true))];
        }

        Dictionary<int, BoundaryPlan> outgoing = [];
        foreach (BoundaryPlan boundary in videoPlan.Boundaries)
        {
            if (!outgoing.TryAdd(boundary.FromClipId, boundary))
            {
                diagnostics.Add(new(
                    AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.boundary.duplicate_from_clip",
                    $"Clip {boundary.FromClipId} has more than one outgoing boundary; only the first is used.",
                    ClipId: boundary.FromClipId));
            }
        }
        ImmutableArray<AudioTimelineClipWindow>.Builder windows = ImmutableArray.CreateBuilder<AudioTimelineClipWindow>();
        double nextStart = 0;
        bool canResolveFollowing = true;
        foreach (ClipPlan clip in videoPlan.Clips)
        {
            int trimFrames = 0;
            bool provisional = false;
            if (outgoing.TryGetValue(clip.ClipId, out BoundaryPlan boundary)
                && boundary.Effective != BoundaryExecutionMode.Cut)
            {
                trimFrames = boundary.Effective == BoundaryExecutionMode.Continue
                    ? boundary.ContinuityWindowFrames
                    : boundary.OverlapFrames;
                provisional = boundary.RequiresRuntimeMergeValidation;
            }

            if (!canResolveFollowing || clip.Frames is not int frames || frames <= 0)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
                    "audio.timeline.clip_timing_unavailable",
                    $"Clip {clip.ClipId} has no usable frame count, so its timeline audio windows cannot be resolved.",
                    ClipId: clip.ClipId));
                windows.Add(new(clip.ClipId, null, null, null, trimFrames, IsProvisional: true));
                canResolveFollowing = false;
                continue;
            }

            double authoredDuration = frames / (double)videoPlan.FramesPerSecond;
            int keptFrames = Math.Max(0, frames - trimFrames);
            if (trimFrames >= frames && trimFrames > 0)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
                    "audio.timeline.clip_trim_exceeds_length",
                    $"Clip {clip.ClipId}'s outgoing boundary trim consumes its complete duration.",
                    ClipId: clip.ClipId));
            }
            double duration = keptFrames / (double)videoPlan.FramesPerSecond;
            windows.Add(new(clip.ClipId, nextStart, duration, authoredDuration, trimFrames, provisional));
            nextStart += duration;
        }
        return windows.ToImmutable();
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
            destination.Add(new(
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
        }
        if (!emitted)
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
                "audio.timeline.span.empty_after_projection",
                "An audio span does not overlap any resolved final-timeline clip window.", trackId, spanIndex));
        }
    }

    private static void AddOverlapDiagnostics(
        ImmutableArray<AudioTimelineTrackPlan>.Builder tracks,
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics)
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
                    diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Info,
                        "audio.timeline.overlapping_tracks",
                        $"Timeline tracks '{first}' and '{second}' overlap in clip {windows[i].ClipId}; execution must mix them.",
                        first,
                        ClipId: windows[i].ClipId));
                }
            }
        }
    }

    /// <summary>
    /// A projected span must consume its source monotonically once: clips are already final-timeline
    /// windows, so a crossfade/continue overlap can never be consumed by both adjacent clip windows.
    /// This is defensive validation of the planner invariant, not a graph-time repair path.
    /// </summary>
    private static void ValidateSpanPartitions(
        ImmutableArray<AudioTimelineTrackPlan>.Builder tracks,
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics)
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
                    diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                        "audio.timeline.span.non_partitioning_projection",
                        "A projected track span would double-consume or skip final timeline time.",
                        group.Key.TrackId,
                        group.Key.SpanIndex));
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
