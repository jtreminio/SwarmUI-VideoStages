using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Pairs a compiled timeline plan with the authored tracks used to build it.
/// </summary>
internal sealed record AudioTimelineCompilation(
    AudioTimelinePlan Plan,
    ImmutableArray<AudioTrackSpec> AuthoredTracks);

/// <summary>
/// Builds final clip windows and projects authored audio tracks.
/// </summary>
internal static class AudioTimelinePlanCompiler
{
    internal static AudioTimelineCompilation Compile(
        VideoExecutionPlan videoPlan,
        IReadOnlyList<TimelineAudioSegmentSpec> authoredSegments)
    {
        ArgumentNullException.ThrowIfNull(videoPlan);
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        ImmutableArray<AudioTimelineClipWindow> clipWindows =
            BuildClipWindows(videoPlan, diagnostics);
        ImmutableArray<AudioTrackSpec> authoredTracks =
            TimelineAudioSegmentTrackSpecPlanner.Compile(authoredSegments, clipWindows);
        return new(
            Project(videoPlan, clipWindows, diagnostics, authoredTracks),
            authoredTracks);
    }

    internal static AudioTimelinePlan Compile(
        VideoExecutionPlan videoPlan,
        ImmutableArray<AudioTrackSpec> tracks = default)
    {
        ArgumentNullException.ThrowIfNull(videoPlan);
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        return Project(
            videoPlan,
            BuildClipWindows(videoPlan, diagnostics),
            diagnostics,
            tracks.IsDefault ? [] : tracks);
    }

    private static AudioTimelinePlan Project(
        VideoExecutionPlan videoPlan,
        ImmutableArray<AudioTimelineClipWindow> clipWindows,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics,
        ImmutableArray<AudioTrackSpec> authoredTracks)
    {
        AudioTimelineTrackProjectionResult trackPlan = AudioTimelineTrackSpanProjector.Project(
            authoredTracks,
            clipWindows,
            IndexClipWindows(clipWindows, diagnostics));
        diagnostics.AddRange(trackPlan.Diagnostics);
        diagnostics.AddRange(AudioTimelineValidationPlanner.Validate(trackPlan.Tracks));
        return new(clipWindows, trackPlan.Tracks, diagnostics.ToImmutable());
    }

    private static ImmutableDictionary<int, int> IndexClipWindows(
        ImmutableArray<AudioTimelineClipWindow> windows,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        ImmutableDictionary<int, int>.Builder indices = ImmutableDictionary.CreateBuilder<int, int>();
        for (int i = 0; i < windows.Length; i++)
        {
            if (!indices.TryAdd(windows[i].ClipId, i))
            {
                diagnostics.Add(new(
                    PlanDiagnosticSeverity.Warning,
                    "audio.timeline.clip.duplicate_id",
                    $"Timeline clip id {windows[i].ClipId} is duplicated; only the first is addressable.",
                    ClipId: windows[i].ClipId));
            }
        }
        return indices.ToImmutable();
    }

    private static ImmutableArray<AudioTimelineClipWindow> BuildClipWindows(
        VideoExecutionPlan videoPlan,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        if (videoPlan.FramesPerSecond <= 0)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.invalid_fps",
                "Timeline audio windows require a positive frames-per-second value."));
            return [.. videoPlan.Clips.Select(clip => new AudioTimelineClipWindow(
                clip.ClipId, null, null))];
        }

        Dictionary<int, BoundaryPlan> outgoing = [];
        foreach (BoundaryPlan boundary in videoPlan.Boundaries)
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

        ImmutableArray<AudioTimelineClipWindow>.Builder windows =
            ImmutableArray.CreateBuilder<AudioTimelineClipWindow>();
        double nextStart = 0;
        bool canResolveFollowing = true;
        foreach (ClipPlan clip in videoPlan.Clips)
        {
            int trimFrames = 0;
            if (outgoing.TryGetValue(clip.ClipId, out BoundaryPlan boundary)
                && boundary.Effective != BoundaryJoinType.Cut)
            {
                trimFrames = BoundaryOverlapPlanner.EffectiveOverlapFrames(boundary);
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
            double duration = keptFrames / (double)videoPlan.FramesPerSecond;
            windows.Add(new(clip.ClipId, nextStart, duration));
            nextStart += duration;
        }
        return windows.ToImmutable();
    }
}
