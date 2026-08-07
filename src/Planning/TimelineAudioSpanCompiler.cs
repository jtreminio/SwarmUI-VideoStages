using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Planning;

internal sealed record TimelineAudioSpanCompilation(
    IReadOnlyList<ClipPlan> Clips,
    ImmutableArray<PlanDiagnostic> Diagnostics);

internal static class TimelineAudioSpanCompiler
{
    internal static TimelineAudioSpanCompilation Compile(
        int framesPerSecond,
        IReadOnlyList<ClipPlan> clips,
        IReadOnlyList<BoundaryPlan> boundaries,
        IReadOnlyList<TimelineAudioSpanSpec> spans)
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
        Dictionary<int, List<AudioSpanPlan>> additions = [];
        HashSet<string> seenIds = new(StringComparer.Ordinal);

        foreach (TimelineAudioSpanSpec span in spans ?? [])
        {
            if (span is null
                || !TryResolveSource(span, out ResolvedSource source)
                || ResolveFinalWindow(span, clipsById) is not { } finalWindow)
            {
                continue;
            }

            string spanId = span.Id?.Trim() ?? "";
            if (spanId.Length == 0)
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.track.missing_id",
                    "A timeline audio track with no id was ignored."));
                continue;
            }
            if (!seenIds.Add(spanId))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.track.duplicate_id",
                    $"Timeline audio track '{spanId}' is duplicated; only the first is used.",
                    TrackId: spanId));
                continue;
            }

            ProjectSegment(
                spanId,
                span,
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
        IReadOnlyDictionary<int, List<AudioSpanPlan>> additions)
    {
        if (!additions.TryGetValue(clip.ClipId, out List<AudioSpanPlan> projected))
        {
            return clip;
        }
        AudioSpanCompilation compiled =
            AudioSpanPlanCompiler.Compile(
                clip.Audio.Spans.AddRange(projected),
                clip.Audio.Base);
        return clip with
        {
            Audio = clip.Audio with
            {
                Spans = compiled.Spans,
                Diagnostics = clip.Audio.Diagnostics.AddRange(compiled.Diagnostics),
            },
        };
    }

    private static void ProjectSegment(
        string spanId,
        TimelineAudioSpanSpec span,
        ResolvedSource source,
        (double Start, double Length) finalWindow,
        ImmutableArray<ClipWindow> clipWindows,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics,
        Dictionary<int, List<AudioSpanPlan>> additions)
    {
        List<(int ClipId, AudioSpanPlan Item)> projected = [];
        bool hasUnresolvedClip = false;
        double spanEnd = finalWindow.Start + finalWindow.Length;
        foreach (ClipWindow clip in clipWindows)
        {
            if (!clip.IsResolved)
            {
                hasUnresolvedClip = true;
                continue;
            }

            double clipStart = clip.TimelineStartSeconds.Value;
            double start = Math.Max(clipStart, finalWindow.Start);
            double end = Math.Min(clipStart + clip.DurationSeconds.Value, spanEnd);
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
                    span.SourceStartSeconds + (start - finalWindow.Start),
                    end - start,
                    source.UploadedMedia,
                    span.Volume)));
        }

        if (hasUnresolvedClip)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.span.unresolved_clip_timing",
                "A timeline audio span cannot be projected while any clip timing is unknown.",
                TrackId: spanId));
            return;
        }
        if (projected.Count == 0)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.span.empty_after_projection",
                "An audio span does not overlap any resolved final-timeline clip window.",
                TrackId: spanId));
            return;
        }
        foreach ((int clipId, AudioSpanPlan item) in projected)
        {
            additions.TryAdd(clipId, []);
            additions[clipId].Add(item);
        }
    }

    private static bool TryResolveSource(
        TimelineAudioSpanSpec span,
        out ResolvedSource source)
    {
        if (MediaSource.TryParseAceStepFunIndex(
                span.AceStepFunSource,
                out int aceStepFunTrack))
        {
            source = new(AudioSourceKind.AceStepFun, aceStepFunTrack, null);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(span.Source?.Data))
        {
            source = new(
                AudioSourceKind.Upload,
                null,
                span.Source);
            return true;
        }
        source = null;
        return false;
    }

    private static (double Start, double Length)? ResolveFinalWindow(
        TimelineAudioSpanSpec span,
        IReadOnlyDictionary<int, ClipWindow> clipsById)
    {
        bool hasAnchors = span.FirstClipId.HasValue
            && span.LastClipId.HasValue
            && span.FirstClipOffsetSeconds.HasValue
            && span.LastClipOffsetSeconds.HasValue;
        if (!hasAnchors
            || !clipsById.TryGetValue(span.FirstClipId.Value, out ClipWindow first)
            || !clipsById.TryGetValue(span.LastClipId.Value, out ClipWindow last)
            || !first.IsResolved
            || !last.IsResolved)
        {
            return (span.TimelineStartSeconds, span.LengthSeconds);
        }

        double start = first.TimelineTimeAt(span.FirstClipOffsetSeconds.Value);
        double length = last.TimelineTimeAt(span.LastClipOffsetSeconds.Value) - start;
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
                && boundary.EffectiveJoin != BoundaryJoinType.Cut)
            {
                trimFrames = Math.Max(
                    0,
                    BoundaryOverlaps.EffectiveOverlapFrames(boundary)
                        - BoundaryOverlaps.IncomingHandleFrames(boundary));
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
        UploadedMediaSpec UploadedMedia);

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
