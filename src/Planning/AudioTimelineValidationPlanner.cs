using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>Validation result for an authored audio track.</summary>
internal sealed record AudioTrackValidation(
    bool CanProject,
    ImmutableArray<PlanDiagnostic> Diagnostics);

/// <summary>
/// Validates track metadata before projection and reports overlap after projection.
/// </summary>
internal static class AudioTimelineValidationPlanner
{
    internal static AudioTrackValidation ValidateTrack(
        AudioTrackSpec track,
        string trackId,
        bool isDuplicateId)
    {
        if (track is null)
        {
            return Rejected(new(
                PlanDiagnosticSeverity.Error,
                "audio.timeline.track.null",
                "A null timeline audio track was ignored."));
        }
        if (trackId.Length == 0)
        {
            return Rejected(new(
                PlanDiagnosticSeverity.Error,
                "audio.timeline.track.missing_id",
                "A timeline audio track needs a non-empty id."));
        }
        if (isDuplicateId)
        {
            return Rejected(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.track.duplicate_id",
                $"Timeline audio track '{trackId}' is duplicated.",
                TrackId: trackId));
        }

        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        if (track.Source is null)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Error,
                "audio.timeline.track.missing_source",
                $"Timeline audio track '{trackId}' has no source.",
                TrackId: trackId));
        }
        if ((track.Spans.IsDefault ? [] : track.Spans).IsEmpty)
        {
            diagnostics.Add(new(
                PlanDiagnosticSeverity.Warning,
                "audio.timeline.track.no_spans",
                $"Timeline audio track '{trackId}' has no spans.",
                TrackId: trackId));
        }
        return new(CanProject: true, diagnostics.ToImmutable());
    }

    /// <summary>Reports cross-track overlap between projected windows.</summary>
    internal static ImmutableArray<PlanDiagnostic> Validate(
        ImmutableArray<AudioTimelineTrackPlan> tracks)
    {
        ImmutableArray<PlanDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<PlanDiagnostic>();
        AddOverlapDiagnostics(tracks, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static AudioTrackValidation Rejected(PlanDiagnostic diagnostic) =>
        new(CanProject: false, [diagnostic]);

    private static void AddOverlapDiagnostics(
        ImmutableArray<AudioTimelineTrackPlan> tracks,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics)
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
                    diagnostics.Add(new(PlanDiagnosticSeverity.Info,
                        "audio.timeline.overlapping_tracks",
                        $"Timeline tracks '{first}' and '{second}' overlap in clip {windows[i].ClipId}; execution must mix them.",
                        TrackId: first,
                        ClipId: windows[i].ClipId));
                }
            }
        }
    }

}
