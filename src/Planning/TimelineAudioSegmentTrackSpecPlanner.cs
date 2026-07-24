using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Adapts the simple root authoring shape to the general timeline projector.</summary>
internal static class TimelineAudioSegmentTrackSpecPlanner
{
    internal static ImmutableArray<AudioTrackSpec> Compile(
        IReadOnlyList<TimelineAudioSegmentSpec> segments,
        VideoExecutionPlan videoPlan)
    {
        IReadOnlyDictionary<int, AudioTimelineClipWindow> clipWindows =
            AudioTimelineClipWindowPlanner
                .Compile(videoPlan)
                .ClipWindows
                .ToDictionary(window => window.ClipId);
        ImmutableArray<AudioTrackSpec>.Builder tracks =
            ImmutableArray.CreateBuilder<AudioTrackSpec>();
        foreach (TimelineAudioSegmentSpec segment in segments ?? [])
        {
            if (segment is null)
            {
                continue;
            }
            AudioTimelineTrackSource source;
            if (AudioHandler.TryParseAceStepFunAudioSource(
                segment.AceStepFunSource,
                out _))
            {
                source = new(
                    AudioTimelineTrackSourceKind.AceStepFun,
                    segment.AceStepFunSource);
            }
            else if (!string.IsNullOrWhiteSpace(segment.Source?.Data))
            {
                AudioMediaIdentityPlan upload = AudioMediaIdentityCompiler.Compile(segment.Source);
                source = new(
                    AudioTimelineTrackSourceKind.Upload,
                    upload.FileName,
                    upload);
            }
            else
            {
                continue;
            }

            (double start, double length) = ResolveFinalWindow(
                segment,
                clipWindows);
            if (!double.IsFinite(start)
                || !double.IsFinite(length)
                || start < 0
                || length <= 0)
            {
                continue;
            }
            tracks.Add(new(
                segment.Id,
                source,
                [new AudioTrackSpanSpec(
                    TimelineStartSeconds: start,
                    TimelineLengthSeconds: length,
                    SourceStartSeconds: segment.SourceStartSeconds)],
                segment.Volume));
        }
        return tracks.ToImmutable();
    }

    private static (double Start, double Length) ResolveFinalWindow(
        TimelineAudioSegmentSpec segment,
        IReadOnlyDictionary<int, AudioTimelineClipWindow> clipWindows)
    {
        bool hasAnchors =
            segment.FirstClipId.HasValue
            && segment.LastClipId.HasValue
            && segment.FirstClipOffsetSeconds.HasValue
            && segment.LastClipOffsetSeconds.HasValue;
        if (!hasAnchors
            || !clipWindows.TryGetValue(
                segment.FirstClipId.Value,
                out AudioTimelineClipWindow first)
            || !clipWindows.TryGetValue(
                segment.LastClipId.Value,
                out AudioTimelineClipWindow last)
            || first.TimelineStartSeconds is not double firstStart
            || first.DurationSeconds is not double firstDuration
            || last.TimelineStartSeconds is not double lastStart
            || last.DurationSeconds is not double lastDuration)
        {
            return (segment.TimelineStartSeconds, segment.LengthSeconds);
        }

        double start = firstStart + Math.Min(
            segment.FirstClipOffsetSeconds.Value,
            firstDuration);
        double end = lastStart + Math.Min(
            segment.LastClipOffsetSeconds.Value,
            lastDuration);
        return (start, end - start);
    }
}
