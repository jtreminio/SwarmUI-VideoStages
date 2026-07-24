using System.Collections.Immutable;

namespace VideoStages.Planning;

internal sealed record AudioTimelineClipWindowPlanningResult(
    ImmutableArray<AudioTimelineClipWindow> ClipWindows,
    ImmutableDictionary<int, int> ClipIndices,
    ImmutableArray<AudioTimelineDiagnostic> Diagnostics);

/// <summary>Projects clip durations and outgoing boundary trims into final timeline clip windows.</summary>
internal static class AudioTimelineClipWindowPlanner
{
    internal static AudioTimelineClipWindowPlanningResult Compile(VideoExecutionPlan videoPlan)
    {
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<AudioTimelineDiagnostic>();
        ImmutableArray<AudioTimelineClipWindow> windows = BuildWindows(videoPlan, diagnostics);
        ImmutableDictionary<int, int>.Builder indices = ImmutableDictionary.CreateBuilder<int, int>();
        for (int i = 0; i < windows.Length; i++)
        {
            if (!indices.TryAdd(windows[i].ClipId, i))
            {
                diagnostics.Add(new(
                    AudioTimelineDiagnosticSeverity.Error,
                    "audio.timeline.clip.duplicate_id",
                    $"Timeline clip id {windows[i].ClipId} is duplicated; only the first is addressable.",
                    ClipId: windows[i].ClipId));
            }
        }
        return new(windows, indices.ToImmutable(), diagnostics.ToImmutable());
    }

    private static ImmutableArray<AudioTimelineClipWindow> BuildWindows(
        VideoExecutionPlan videoPlan,
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics)
    {
        if (videoPlan.FramesPerSecond <= 0)
        {
            diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Error,
                "audio.timeline.invalid_fps", "Timeline audio windows require a positive frames-per-second value."));
            return [.. videoPlan.Clips.Select(clip => new AudioTimelineClipWindow(
                clip.ClipId, null, null))];
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

        ImmutableArray<AudioTimelineClipWindow>.Builder windows =
            ImmutableArray.CreateBuilder<AudioTimelineClipWindow>();
        double nextStart = 0;
        bool canResolveFollowing = true;
        foreach (ClipPlan clip in videoPlan.Clips)
        {
            int trimFrames = 0;
            if (outgoing.TryGetValue(clip.ClipId, out BoundaryPlan boundary)
                && boundary.Effective != BoundaryExecutionMode.Cut)
            {
                trimFrames = BoundaryOverlapPlanner.EffectiveOverlapFrames(boundary);
            }

            if (!canResolveFollowing || clip.Frames is not int frames || frames <= 0)
            {
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
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
                diagnostics.Add(new(AudioTimelineDiagnosticSeverity.Warning,
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
