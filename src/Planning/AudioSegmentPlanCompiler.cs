using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Compiles independently ordered audio segment windows and their base-track requirement.</summary>
internal static class AudioSegmentPlanCompiler
{
    private const string SegmentsWithoutBase = "audio.segments.preserve_windowed_no_base";
    private const string SegmentIgnoredNoSource = "audio.segment.ignored_no_source";
    private const string SegmentIgnoredInvalidWindow = "audio.segment.ignored_invalid_window";

    internal static AudioPlanComponentResult<AudioSegmentPlan> Compile(
        ClipSpec clip,
        AudioBaseSourcePlan baseSource)
    {
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<AudioPlanDiagnostic>();
        ImmutableArray<AudioSegmentItemPlan>.Builder items = ImmutableArray.CreateBuilder<AudioSegmentItemPlan>();
        foreach (AudioSegmentSpec segment in clip.AudioSegments ?? [])
        {
            if (segment is null)
            {
                diagnostics.Add(new(SegmentIgnoredNoSource, "An audio segment has no source and was ignored."));
                continue;
            }
            if (double.IsNaN(segment.StartSeconds) || double.IsInfinity(segment.StartSeconds)
                || double.IsNaN(segment.TrimStartSeconds) || double.IsInfinity(segment.TrimStartSeconds)
                || double.IsNaN(segment.LengthSeconds) || double.IsInfinity(segment.LengthSeconds)
                || segment.StartSeconds < 0 || segment.TrimStartSeconds < 0 || segment.LengthSeconds <= 0)
            {
                diagnostics.Add(new(SegmentIgnoredInvalidWindow, "An audio segment has an invalid time window and was ignored."));
                continue;
            }
            if (AudioHandler.TryParseAceStepFunAudioSource(segment.AceStepFunSource, out int aceTrack))
            {
                items.Add(new(AudioSegmentSourceKind.AceStepFun, aceTrack,
                    segment.StartSeconds, segment.TrimStartSeconds, segment.LengthSeconds, null,
                    segment.Volume));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(segment.Source?.Data))
            {
                items.Add(new(AudioSegmentSourceKind.Upload, null,
                    segment.StartSeconds, segment.TrimStartSeconds, segment.LengthSeconds,
                    AudioMediaIdentityCompiler.Compile(segment.Source),
                    segment.Volume));
                continue;
            }
            diagnostics.Add(new(SegmentIgnoredNoSource, "An audio segment has no usable upload or AceStepFun source and was ignored."));
        }

        ImmutableArray<AudioSegmentItemPlan> ordered = items
            .OrderBy(item => item.StartSeconds)
            .ThenBy(item => item.TrimStartSeconds)
            .ToImmutableArray();
        if (ordered.IsEmpty)
        {
            return new(
                new(ordered),
                diagnostics.ToImmutable());
        }
        if (!baseSource.HasConfiguredTrack)
        {
            diagnostics.Add(new(
                SegmentsWithoutBase,
                "Audio segments have no locked base track, so only their windows are preserved and gaps are generated."));
            return new(
                new(ordered),
                diagnostics.ToImmutable());
        }

        return new(new(ordered), diagnostics.ToImmutable());
    }
}
