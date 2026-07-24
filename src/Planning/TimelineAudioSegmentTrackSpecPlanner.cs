using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Adapts the simple root authoring shape to the general timeline projector.</summary>
internal static class TimelineAudioSegmentTrackSpecPlanner
{
    internal static ImmutableArray<AudioTrackSpec> Compile(
        IReadOnlyList<TimelineAudioSegmentSpec> segments,
        ImmutableArray<AudioTimelineClipWindow> windows)
    {
        IReadOnlyDictionary<int, AudioTimelineClipWindow> clipWindows =
            windows.ToDictionary(window => window.ClipId);
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
                    AudioSourceKind.AceStepFun,
                    segment.AceStepFunSource);
            }
            else if (!string.IsNullOrWhiteSpace(segment.Source?.Data))
            {
                AudioMediaIdentityPlan upload = AudioMediaIdentityPlan.From(segment.Source);
                source = new(
                    AudioSourceKind.Upload,
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
            || !first.IsResolved
            || !last.IsResolved)
        {
            return (segment.TimelineStartSeconds, segment.LengthSeconds);
        }

        double start = first.TimelineTimeAt(segment.FirstClipOffsetSeconds.Value);
        double end = last.TimelineTimeAt(segment.LastClipOffsetSeconds.Value);
        return (start, end - start);
    }
}
