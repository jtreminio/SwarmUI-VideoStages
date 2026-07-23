using System.Collections.Immutable;

namespace VideoStages.Planning;

/// <summary>
/// Thin composition facade for the graph-independent audio timeline planners. Runtime mixing stays
/// outside this compiler; these planners only turn authored tracks into final timeline windows.
/// </summary>
internal static class AudioTimelinePlanCompiler
{
    public static AudioTimelinePlan Compile(
        VideoExecutionPlan videoPlan,
        ImmutableArray<AudioTrackSpec> tracks = default)
    {
        ArgumentNullException.ThrowIfNull(videoPlan);

        AudioTimelineClipWindowPlanningResult clipWindowPlan = AudioTimelineClipWindowPlanner.Compile(videoPlan);
        ImmutableArray<AudioTrackSpec> authoredTracks = tracks.IsDefault ? [] : tracks;
        ImmutableArray<AudioTrackSpec> allTracks = ClipAudioTrackSpecPlanner
            .Compile(videoPlan)
            .AddRange(authoredTracks);
        AudioTimelineTrackProjectionResult trackPlan = AudioTimelineTrackSpanProjector.Project(
            allTracks,
            clipWindowPlan.ClipWindows,
            clipWindowPlan.ClipIndices);
        ImmutableArray<AudioTimelineDiagnostic> validationDiagnostics =
            AudioTimelineValidationPlanner.Validate(trackPlan.Tracks);

        return new(
            clipWindowPlan.ClipWindows,
            trackPlan.Tracks,
            clipWindowPlan.Diagnostics
                .AddRange(trackPlan.Diagnostics)
                .AddRange(validationDiagnostics));
    }
}
