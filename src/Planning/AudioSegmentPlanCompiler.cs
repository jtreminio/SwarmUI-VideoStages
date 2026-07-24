using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Orders projected audio segment windows and reports their base-track requirement.</summary>
internal static class AudioSegmentPlanCompiler
{
    private const string SegmentsWithoutBase = "audio.segments.preserve_windowed_no_base";
    private const string SegmentIgnoredNoSource = "audio.segment.ignored_no_source";
    private const string SegmentIgnoredInvalidWindow = "audio.segment.ignored_invalid_window";

    internal static AudioPlanComponentResult<AudioSegmentPlan> Compile(
        IEnumerable<AudioSegmentItemPlan> segments,
        AudioBaseSourcePlan baseSource)
    {
        ImmutableArray<AudioPlanDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<AudioPlanDiagnostic>();
        ImmutableArray<AudioSegmentItemPlan>.Builder items = ImmutableArray.CreateBuilder<AudioSegmentItemPlan>();
        foreach (AudioSegmentItemPlan segment in segments ?? [])
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
            if (segment.SourceKind == AudioSegmentSourceKind.AceStepFun
                && segment.AceStepFunTrack is null)
            {
                diagnostics.Add(new(SegmentIgnoredNoSource, "An audio segment has no usable AceStepFun track and was ignored."));
                continue;
            }
            if (segment.SourceKind == AudioSegmentSourceKind.Upload
                && string.IsNullOrWhiteSpace(segment.UploadedMedia?.Data))
            {
                diagnostics.Add(new(SegmentIgnoredNoSource, "An audio segment has no usable upload source and was ignored."));
                continue;
            }
            items.Add(segment);
        }

        ImmutableArray<AudioSegmentItemPlan> ordered = items
            .OrderBy(item => item.StartSeconds)
            .ThenBy(item => item.TrimStartSeconds)
            .ToImmutableArray();
        if (!ordered.IsEmpty && !baseSource.HasConfiguredTrack)
        {
            diagnostics.Add(new(
                SegmentsWithoutBase,
                "Audio segments have no locked base track, so only their windows are preserved and gaps are generated."));
        }
        return new(new(ordered), diagnostics.ToImmutable());
    }
}
