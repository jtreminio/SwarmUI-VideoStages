using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Performs graph-free partition and cross-track overlap validation of projected windows.</summary>
internal static class AudioTimelineValidationPlanner
{
    internal static ImmutableArray<AudioTimelineDiagnostic> Validate(
        ImmutableArray<AudioTimelineTrackPlan> tracks)
    {
        ImmutableArray<AudioTimelineDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<AudioTimelineDiagnostic>();
        ValidateSpanPartitions(tracks, diagnostics);
        AddOverlapDiagnostics(tracks, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static void AddOverlapDiagnostics(
        ImmutableArray<AudioTimelineTrackPlan> tracks,
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

    private static void ValidateSpanPartitions(
        ImmutableArray<AudioTimelineTrackPlan> tracks,
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
}
