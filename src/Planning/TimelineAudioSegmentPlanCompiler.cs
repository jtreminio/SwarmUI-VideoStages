using System.Collections.Immutable;

namespace VideoStages.Planning;

internal sealed record TimelineAudioSegmentCompilation(
    IReadOnlyList<ClipPlan> Clips,
    ImmutableArray<PlanDiagnostic> Diagnostics);

internal static class TimelineAudioSegmentPlanCompiler
{
    internal static TimelineAudioSegmentCompilation Compile(
        int framesPerSecond,
        IReadOnlyList<ClipPlan> clips,
        IReadOnlyList<BoundaryPlan> boundaries,
        IReadOnlyList<TimelineAudioSegmentSpec> segments)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(boundaries);
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        ImmutableArray<ClipWindow> clipWindows = BuildClipWindows(
            framesPerSecond,
            clips,
            boundaries,
            diagnostics);
        IReadOnlyDictionary<int, ClipWindow> clipsById = IndexClipWindows(
            clipWindows,
            diagnostics);
        Dictionary<int, List<AudioSegmentItemPlan>> additions = [];
        HashSet<string> seenIds = new(StringComparer.Ordinal);

        foreach (TimelineAudioSegmentSpec segment in segments ?? [])
        {
            if (segment is null
                || !TryResolveSource(segment, out ResolvedSource source)
                || ResolveFinalWindow(segment, clipsById) is not { } finalWindow)
            {
                continue;
            }

            string segmentId = segment.Id?.Trim() ?? "";
            if (segmentId.Length == 0)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.track.missing_id",
                    "A timeline audio track with no id was ignored."));
                continue;
            }
            if (!seenIds.Add(segmentId))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.track.duplicate_id",
                    $"Timeline audio track '{segmentId}' is duplicated; only the first is used.",
                    TrackId: segmentId));
                continue;
            }

            ProjectSegment(
                segmentId,
                segment,
                source,
                finalWindow,
                clipWindows,
                diagnostics,
                additions);
        }

        return new(
            Array.AsReadOnly(clips.Select(clip => AppendSegments(clip, additions)).ToArray()),
            diagnostics.ToImmutable());
    }

    private static ClipPlan AppendSegments(
        ClipPlan clip,
        IReadOnlyDictionary<int, List<AudioSegmentItemPlan>> additions)
    {
        if (!additions.TryGetValue(clip.ClipId, out List<AudioSegmentItemPlan> projected))
        {
            return clip;
        }
        AudioPlanComponentResult<AudioSegmentPlan> compiled =
            AudioSegmentPlanCompiler.Compile(
                clip.Audio.Segments.Items.AddRange(projected),
                clip.Audio.Base);
        return clip with
        {
            Audio = clip.Audio with
            {
                Segments = compiled.Plan,
                Diagnostics = clip.Audio.Diagnostics.AddRange(compiled.Diagnostics),
            },
        };
    }

    private static void ProjectSegment(
        string segmentId,
        TimelineAudioSegmentSpec segment,
        ResolvedSource source,
        (double Start, double Length) finalWindow,
        ImmutableArray<ClipWindow> clipWindows,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics,
        Dictionary<int, List<AudioSegmentItemPlan>> additions)
    {
        List<(int ClipId, AudioSegmentItemPlan Item)> projected = [];
        bool hasUnresolvedClip = false;
        double segmentEnd = finalWindow.Start + finalWindow.Length;
        foreach (ClipWindow clip in clipWindows)
        {
            if (!clip.IsResolved)
            {
                hasUnresolvedClip = true;
                continue;
            }

            double clipStart = clip.TimelineStartSeconds.Value;
            double start = Math.Max(clipStart, finalWindow.Start);
            double end = Math.Min(clipStart + clip.DurationSeconds.Value, segmentEnd);
            if (end <= start)
            {
                continue;
            }
            projected.Add((
                clip.ClipId,
                new(
                    source.Kind,
                    source.AceStepFunTrack,
                    start - clipStart,
                    segment.SourceStartSeconds + (start - finalWindow.Start),
                    end - start,
                    source.UploadedMedia,
                    segment.Volume)));
        }

        if (hasUnresolvedClip)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.span.unresolved_clip_timing",
                "A timeline audio segment cannot be projected while any clip timing is unknown.",
                TrackId: segmentId,
                SpanIndex: 0));
            return;
        }
        if (projected.Count == 0)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.span.empty_after_projection",
                "An audio span does not overlap any resolved final-timeline clip window.",
                TrackId: segmentId,
                SpanIndex: 0));
            return;
        }
        foreach ((int clipId, AudioSegmentItemPlan item) in projected)
        {
            additions.TryAdd(clipId, []);
            additions[clipId].Add(item);
        }
    }

    private static bool TryResolveSource(
        TimelineAudioSegmentSpec segment,
        out ResolvedSource source)
    {
        if (AudioHandler.TryParseAceStepFunAudioSource(
                segment.AceStepFunSource,
                out int aceStepFunTrack))
        {
            source = new(AudioSourceKind.AceStepFun, aceStepFunTrack, null);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(segment.Source?.Data))
        {
            source = new(
                AudioSourceKind.Upload,
                null,
                AudioMediaIdentityPlan.From(segment.Source));
            return true;
        }
        source = null;
        return false;
    }

    private static (double Start, double Length)? ResolveFinalWindow(
        TimelineAudioSegmentSpec segment,
        IReadOnlyDictionary<int, ClipWindow> clipsById)
    {
        bool hasAnchors = segment.FirstClipId.HasValue
            && segment.LastClipId.HasValue
            && segment.FirstClipOffsetSeconds.HasValue
            && segment.LastClipOffsetSeconds.HasValue;
        if (!hasAnchors
            || !clipsById.TryGetValue(segment.FirstClipId.Value, out ClipWindow first)
            || !clipsById.TryGetValue(segment.LastClipId.Value, out ClipWindow last)
            || !first.IsResolved
            || !last.IsResolved)
        {
            return (segment.TimelineStartSeconds, segment.LengthSeconds);
        }

        double start = first.TimelineTimeAt(segment.FirstClipOffsetSeconds.Value);
        double length = last.TimelineTimeAt(segment.LastClipOffsetSeconds.Value) - start;
        return double.IsFinite(start)
            && double.IsFinite(length)
            && start >= 0
            && length > 0
                ? (start, length)
                : null;
    }

    private static IReadOnlyDictionary<int, ClipWindow> IndexClipWindows(
        ImmutableArray<ClipWindow> windows,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        Dictionary<int, ClipWindow> indexed = [];
        foreach (ClipWindow window in windows)
        {
            if (!indexed.TryAdd(window.ClipId, window))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.clip.duplicate_id",
                    $"Timeline clip id {window.ClipId} is duplicated; only the first is addressable.",
                    ClipId: window.ClipId));
            }
        }
        return indexed;
    }

    private static ImmutableArray<ClipWindow> BuildClipWindows(
        int framesPerSecond,
        IReadOnlyList<ClipPlan> clips,
        IReadOnlyList<BoundaryPlan> boundaries,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        if (framesPerSecond <= 0)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.invalid_fps",
                "Timeline audio windows require a positive frames-per-second value."));
            return [.. clips.Select(clip => new ClipWindow(clip.ClipId, null, null))];
        }

        Dictionary<int, BoundaryPlan> outgoing = [];
        foreach (BoundaryPlan boundary in boundaries)
        {
            if (!outgoing.TryAdd(boundary.FromClipId, boundary))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.boundary.duplicate_from_clip",
                    $"Clip {boundary.FromClipId} has more than one outgoing boundary; only the first is used.",
                    ClipId: boundary.FromClipId));
            }
        }

        ImmutableArray<ClipWindow>.Builder windows = ImmutableArray.CreateBuilder<ClipWindow>();
        double nextStart = 0;
        bool canResolveFollowing = true;
        foreach (ClipPlan clip in clips)
        {
            int trimFrames = 0;
            if (outgoing.TryGetValue(clip.ClipId, out BoundaryPlan boundary)
                && boundary.Effective != BoundaryJoinType.Cut)
            {
                trimFrames = BoundaryOverlapPlanner.TimelineReductionFrames(boundary);
            }
            if (!canResolveFollowing || clip.Frames is not int frames || frames <= 0)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.clip_timing_unavailable",
                    $"Clip {clip.ClipId} has no usable frame count, so its timeline audio windows cannot be resolved.",
                    ClipId: clip.ClipId));
                windows.Add(new(clip.ClipId, null, null));
                canResolveFollowing = false;
                continue;
            }

            int keptFrames = Math.Max(0, frames - trimFrames);
            if (trimFrames >= frames && trimFrames > 0)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.clip_trim_exceeds_length",
                    $"Clip {clip.ClipId}'s outgoing boundary trim consumes its complete duration.",
                    ClipId: clip.ClipId));
            }
            double duration = keptFrames / (double)framesPerSecond;
            windows.Add(new(clip.ClipId, nextStart, duration));
            nextStart += duration;
        }
        return windows.ToImmutable();
    }

    private sealed record ResolvedSource(
        AudioSourceKind Kind,
        int? AceStepFunTrack,
        AudioMediaIdentityPlan UploadedMedia);

    private sealed record ClipWindow(
        int ClipId,
        double? TimelineStartSeconds,
        double? DurationSeconds)
    {
        internal bool IsResolved => TimelineStartSeconds.HasValue && DurationSeconds.HasValue;

        internal double TimelineTimeAt(double clipOffsetSeconds) =>
            TimelineStartSeconds.Value + Math.Clamp(clipOffsetSeconds, 0, DurationSeconds.Value);
    }
}
