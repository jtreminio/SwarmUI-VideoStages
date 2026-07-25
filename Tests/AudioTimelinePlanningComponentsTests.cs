using System.Collections.Immutable;
using System.Text.Json;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class AudioTimelinePlanningComponentsTests
{
    private const int Fps = 24;

    private static StageSpec Stage(int id) => new(
        id, 1, 1, "pixel-lanczos", "ltx-2", 8, 1, "euler", "normal", "Generated");

    private static ClipSpec Clip(
        int id,
        int? frames = 48,
        string boundary = Constants.BoundaryOutCut,
        int overlap = 8) => new(
            id,
            frames,
            Constants.AudioSourceNative,
            [],
            false,
            false,
            false,
            false,
            null,
            [],
            [Stage(id)],
            BoundaryOut: boundary,
            BoundaryOutOverlap: overlap);

    private static VideoExecutionPlan Plan(params ClipSpec[] clips) =>
        TestPlanCompiler.Compile(new VideoStagesSpec(512, 512, Fps, false, clips));

    private static AudioTrackSpec Track(string id, params AudioTrackSpanSpec[] spans) => new(
        id,
        new AudioTimelineTrackSource(AudioSourceKind.External, $"{id}.wav"),
        [.. spans]);

    [Fact]
    public void Clip_window_planner_uses_boundary_trims_and_marks_runtime_validated_seams_provisional()
    {
        VideoExecutionPlan video = Plan(
            Clip(0, frames: 49, boundary: Constants.BoundaryOutContinue, overlap: 8),
            Clip(1, frames: 49));

        AudioTimelinePlan result = AudioTimelinePlanCompiler.Compile(video);

        Assert.Equal(40d / Fps, result.ClipWindows[0].DurationSeconds!.Value, 8);
        Assert.Equal(40d / Fps, result.ClipWindows[1].TimelineStartSeconds!.Value, 8);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Compatibility_track_spec_planner_expands_clip_base_without_projection()
    {
        ImmutableArray<AudioTrackSpec> tracks =
            ClipAudioTrackSpecPlanner.Compile(Plan(Clip(4)));

        Assert.Equal(["clip-4-base"], tracks.Select(track => track.TrackId));
        AudioTrackSpanSpec span = Assert.Single(tracks[0].Spans);
        Assert.Equal(4, span.FirstClipId);
        Assert.Equal(4, span.LastClipId);
    }

    [Fact]
    public void Span_projector_keeps_cross_clip_windows_and_unresolved_clip_relative_spans_atomic()
    {
        ImmutableArray<AudioTimelineClipWindow> clipWindows =
        [
            new(0, 0, 2),
            new(1, 2, 2),
            new(2, null, null),
        ];
        ImmutableDictionary<int, int> clipIndices = ImmutableDictionary<int, int>.Empty
            .Add(0, 0)
            .Add(1, 1)
            .Add(2, 2);

        AudioTimelineTrackProjectionResult result = AudioTimelineTrackSpanProjector.Project(
        [
            Track("score", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1, SourceStartSeconds: 3)),
            Track("pending", new AudioTrackSpanSpec(
                FirstClipId: 2,
                LastClipId: 2,
                ClipStartOffsetSeconds: 0.5,
                ClipLengthSeconds: 1)),
        ],
        clipWindows,
        clipIndices);

        AudioTimelineTrackPlan score = Assert.Single(result.Tracks.Where(track => track.TrackId == "score"));
        Assert.Equal([0, 1], score.Windows.Select(window => window.ClipId));
        Assert.Equal([3d, 5d], score.Windows.Select(window => window.SourceStartSeconds));

        AudioTimelineTrackPlan pending = Assert.Single(result.Tracks.Where(track => track.TrackId == "pending"));
        Assert.Empty(pending.Windows);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.span.unresolved_clip_relative_timing");
    }

    [Fact]
    public void Span_projector_keeps_a_partially_unresolved_cross_clip_span_fully_pending()
    {
        ImmutableArray<AudioTimelineClipWindow> clipWindows =
        [
            new(0, 0, 2),
            new(1, null, null),
        ];
        ImmutableDictionary<int, int> clipIndices = ImmutableDictionary<int, int>.Empty
            .Add(0, 0)
            .Add(1, 1);

        AudioTimelineTrackProjectionResult result = AudioTimelineTrackSpanProjector.Project(
        [
            Track("score", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1)),
        ],
        clipWindows,
        clipIndices);

        AudioTimelineTrackPlan score = Assert.Single(result.Tracks);
        Assert.Empty(score.Windows);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.span.unresolved_clip_timing");
    }

    [Fact]
    public void Validation_planner_reports_partition_errors_before_cross_track_overlap_information()
    {
        AudioTimelineTrackSource source = new(AudioSourceKind.External, "source.wav");
        ImmutableArray<AudioTimelineTrackPlan> tracks =
        [
            new("first", source,
            [
                new("first", 0, 0, 0, 2, 0),
                new("first", 0, 0, 1, 1, 3),
            ]),
            new("second", source,
            [new("second", 0, 0, 0.5, 1, 0)]),
        ];

        ImmutableArray<PlanDiagnostic> diagnostics = AudioTimelineValidationPlanner.Validate(tracks);

        Assert.Equal(
            ["audio.timeline.span.non_partitioning_projection", "audio.timeline.overlapping_tracks"],
            diagnostics.Select(diagnostic => diagnostic.Code));
    }

    public static IEnumerable<object[]> FacadeParityCases()
    {
        yield return ["single clip", Plan(Clip(0)), ImmutableArray<AudioTrackSpec>.Empty];
        yield return ["cross clip track", Plan(Clip(0), Clip(1)), ImmutableArray.Create(
            Track("score", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1)))];
        yield return ["overlap", Plan(Clip(0), Clip(1)), ImmutableArray.Create(
            Track("music", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1)),
            Track("voice", new AudioTrackSpanSpec(TimelineStartSeconds: 1, TimelineLengthSeconds: 2)))];
        yield return ["provisional boundary", Plan(
            Clip(0, frames: 49, boundary: Constants.BoundaryOutCrossfade, overlap: 8),
            Clip(1)), ImmutableArray.Create(
            Track("score", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1)))];
        yield return ["unknown timing", Plan(Clip(0), Clip(1, frames: null)), ImmutableArray.Create(
            Track("score", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1)))];
        VideoExecutionPlan duplicateClips = Plan(Clip(0)) with { Clips = [Plan(Clip(0)).Clips[0], Plan(Clip(0)).Clips[0]] };
        yield return ["duplicate clips", duplicateClips, ImmutableArray.Create(
            Track("score", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 0)))];
        yield return ["duplicate tracks", Plan(Clip(0)), ImmutableArray.Create(
            Track("duplicate", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 0)),
            Track("duplicate", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 0, SourceStartSeconds: 9)))];
    }

    [Theory]
    [MemberData(nameof(FacadeParityCases))]
    public void Compiler_is_deterministic_and_projects_each_clip_window_once(
        string _,
        object videoInput,
        object tracksInput)
    {
        VideoExecutionPlan video = Assert.IsType<VideoExecutionPlan>(videoInput);
        ImmutableArray<AudioTrackSpec> tracks = Assert.IsType<ImmutableArray<AudioTrackSpec>>(tracksInput);

        AudioTimelinePlan actual = AudioTimelinePlanCompiler.Compile(video, tracks);

        Assert.Equal(
            Serialize(AudioTimelinePlanCompiler.Compile(video, tracks)),
            Serialize(actual));
        Assert.Equal(
            video.Clips.Select(clip => clip.ClipId),
            actual.ClipWindows.Select(window => window.ClipId));
        // The clip-window pass used to run twice per plan; every window diagnostic must be unique.
        Assert.All(
            actual.Diagnostics
                .Where(diagnostic => diagnostic.Code.StartsWith("audio.timeline.clip"))
                .GroupBy(diagnostic => (diagnostic.Code, diagnostic.ClipId)),
            group => Assert.Single(group));
    }

    private static string Serialize(AudioTimelinePlan plan) =>
        JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
}
