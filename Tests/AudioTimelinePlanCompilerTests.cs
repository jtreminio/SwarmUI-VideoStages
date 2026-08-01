using System.Collections.Immutable;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class AudioTimelinePlanCompilerTests
{
    private const int Fps = 24;
    private const int Frames = 49;
    private const double ClipSeconds = Frames / (double)Fps;

    private static StageSpec Stage(int id) => new(
        id, 1, 1, "pixel-lanczos", "ltx-2", 8, 1, "euler", "normal", "Generated");

    private static ClipSpec Clip(
        int id,
        int? frames = Frames,
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

    private static AudioTimelineTrackPlan Track(AudioTimelinePlan plan, string id) =>
        Assert.Single(plan.Tracks.Where(track => track.TrackId == id));

    [Fact]
    public void Whole_timeline_track_partitions_across_all_clips()
    {
        VideoExecutionPlan video = Plan(Clip(0), Clip(1), Clip(2));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
            [Track("music", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 2))]);

        AudioTrackClipWindow[] windows = Track(timeline, "music").Windows.ToArray();
        Assert.Equal(3, windows.Length);
        Assert.Equal([0, 1, 2], windows.Select(window => window.ClipId));
        Assert.Equal(
            [0d, ClipSeconds, ClipSeconds * 2],
            windows.Select(window => window.TimelineStartSeconds));
        Assert.Equal(
            [0d, ClipSeconds, ClipSeconds * 2],
            windows.Select(window => window.SourceStartSeconds));
        Assert.All(
            windows,
            window => Assert.Equal(ClipSeconds, window.DurationSeconds, 8));
    }

    [Fact]
    public void One_clip_track_projects_only_the_owned_clip()
    {
        VideoExecutionPlan video = Plan(Clip(0), Clip(1), Clip(2));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
            [Track("stinger", new AudioTrackSpanSpec(FirstClipId: 1, LastClipId: 1, SourceStartSeconds: 3))]);

        AudioTrackClipWindow window = Assert.Single(Track(timeline, "stinger").Windows);
        Assert.Equal(1, window.ClipId);
        Assert.Equal(ClipSeconds, window.TimelineStartSeconds, 8);
        Assert.Equal(ClipSeconds, window.DurationSeconds, 8);
        Assert.Equal(3, window.SourceStartSeconds, 8);
    }

    [Fact]
    public void Partial_multi_clip_timeline_window_intersects_and_advances_source_time()
    {
        VideoExecutionPlan video = Plan(Clip(0), Clip(1), Clip(2));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
            [Track("dialogue", new AudioTrackSpanSpec(
                TimelineStartSeconds: 1.5,
                TimelineLengthSeconds: 3,
                SourceStartSeconds: 10))]);

        AudioTrackClipWindow[] windows = Track(timeline, "dialogue").Windows.ToArray();
        Assert.Equal(3, windows.Length);
        Assert.Equal([0, 1, 2], windows.Select(window => window.ClipId));
        double firstDuration = ClipSeconds - 1.5;
        Assert.Equal(
            [1.5d, ClipSeconds, ClipSeconds * 2],
            windows.Select(window => window.TimelineStartSeconds));
        Assert.Equal(
            [firstDuration, ClipSeconds, 4.5 - ClipSeconds * 2],
            windows.Select(window => window.DurationSeconds));
        Assert.Equal(
            [10d, 10 + firstDuration, 10 + firstDuration + ClipSeconds],
            windows.Select(window => window.SourceStartSeconds));
    }

    [Fact]
    public void Clip_range_and_timeline_window_use_their_intersection()
    {
        VideoExecutionPlan video = Plan(Clip(0), Clip(1), Clip(2));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
            [Track("bounded", new AudioTrackSpanSpec(
                FirstClipId: 1,
                LastClipId: 2,
                TimelineStartSeconds: 1,
                TimelineLengthSeconds: 3.5))]);

        AudioTrackClipWindow[] windows = Track(timeline, "bounded").Windows.ToArray();
        Assert.Equal([1, 2], windows.Select(window => window.ClipId));
        Assert.Equal(
            [ClipSeconds, ClipSeconds * 2],
            windows.Select(window => window.TimelineStartSeconds));
        Assert.Equal(
            [ClipSeconds, 4.5 - ClipSeconds * 2],
            windows.Select(window => window.DurationSeconds));
    }

    [Fact]
    public void Overlapping_tracks_remain_independent()
    {
        VideoExecutionPlan video = Plan(Clip(0), Clip(1));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
        [
            Track("music", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1)),
            Track("voice", new AudioTrackSpanSpec(TimelineStartSeconds: 1, TimelineLengthSeconds: 2)),
        ]);

        Assert.Equal(2, Track(timeline, "music").Windows.Length);
        Assert.Equal(2, Track(timeline, "voice").Windows.Length);
    }

    [Fact]
    public void Transition_trims_are_shared_by_video_and_audio_track_projection()
    {
        VideoExecutionPlan video = Plan(
            Clip(0, frames: 49, boundary: Constants.BoundaryOutContinue, overlap: 8),
            Clip(1, frames: 49, boundary: Constants.BoundaryOutCrossfade, overlap: 16),
            Clip(2, frames: 49));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
            [Track("score", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 2))]);

        AudioTimelineClipWindow[] clips = timeline.ClipWindows.ToArray();
        Assert.Equal(40d / Fps, clips[0].DurationSeconds!.Value, 8);
        Assert.Equal(33d / Fps, clips[1].DurationSeconds!.Value, 8);

        AudioTrackClipWindow[] windows = Track(timeline, "score").Windows.ToArray();
        Assert.Equal(0, windows[0].TimelineStartSeconds, 8);
        Assert.Equal(40d / Fps, windows[1].TimelineStartSeconds, 8);
        Assert.Equal(73d / Fps, windows[2].TimelineStartSeconds, 8);
        Assert.Equal(0, windows[0].SourceStartSeconds, 8);
        Assert.Equal(40d / Fps, windows[1].SourceStartSeconds, 8);
        Assert.Equal(73d / Fps, windows[2].SourceStartSeconds, 8);
    }

    [Fact]
    public void Proper_subset_track_advances_source_by_final_trimmed_timeline_duration()
    {
        VideoExecutionPlan video = Plan(
            Clip(0, frames: 49),
            Clip(1, frames: 49, boundary: Constants.BoundaryOutContinue, overlap: 8),
            Clip(2, frames: 49),
            Clip(3, frames: 49));
        const double sourceStart = 7.5;
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
            [Track("subset", new AudioTrackSpanSpec(
                FirstClipId: 1,
                LastClipId: 3,
                SourceStartSeconds: sourceStart))]);

        AudioTrackClipWindow[] windows = Track(timeline, "subset").Windows.ToArray();
        Assert.Equal([1, 2, 3], windows.Select(window => window.ClipId));
        Assert.Equal(sourceStart, windows[0].SourceStartSeconds, 8);
        Assert.Equal(sourceStart + 40d / Fps, windows[1].SourceStartSeconds, 8);
        Assert.Equal(sourceStart + 89d / Fps, windows[2].SourceStartSeconds, 8);

        double finalSourceEnd = windows[^1].SourceStartSeconds + windows[^1].DurationSeconds;
        double finalTimelineDuration = windows.Sum(window => window.DurationSeconds);
        Assert.Equal(sourceStart + finalTimelineDuration, finalSourceEnd, 8);
    }

    [Fact]
    public void Unknown_clips_and_unresolved_timing_produce_stable_diagnostics()
    {
        VideoExecutionPlan video = Plan(Clip(0), Clip(1, frames: null));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
        [
            Track("bad-owner", new AudioTrackSpanSpec()),
            Track("bad-clip", new AudioTrackSpanSpec(FirstClipId: 99, LastClipId: 99)),
            Track("bad-window", new AudioTrackSpanSpec(TimelineStartSeconds: 1)),
            Track("unresolved", new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 1)),
        ]);

        string[] codes = [.. timeline.Diagnostics.Select(diagnostic => diagnostic.Code)];
        Assert.Contains("audio.timeline.span.unknown_first_clip", codes);
        Assert.Contains("audio.timeline.clip_timing_unavailable", codes);
        Assert.Contains("audio.timeline.span.unresolved_clip_timing", codes);
        Assert.Contains(timeline.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.span.unknown_first_clip"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Duplicate_track_ids_reject_later_track_and_keep_first()
    {
        VideoExecutionPlan video = Plan(Clip(0));
        AudioTrackSpec first = Track(
            "duplicate",
            new AudioTrackSpanSpec(FirstClipId: 0, LastClipId: 0));
        AudioTrackSpec second = Track(
            "duplicate",
            new AudioTrackSpanSpec(
                FirstClipId: 0,
                LastClipId: 0,
                SourceStartSeconds: 9));

        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video, [first, second]);

        AudioTimelineTrackPlan retained = Track(timeline, "duplicate");
        Assert.Equal(0, Assert.Single(retained.Windows).SourceStartSeconds);
        Assert.Contains(timeline.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.track.duplicate_id"
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Invalid_track_metadata_warns_and_skips_unusable_tracks()
    {
        VideoExecutionPlan video = Plan(Clip(0));
        AudioTimelineTrackSource source = new(AudioSourceKind.External, "source.wav");
        ImmutableArray<AudioTrackSpec> tracks =
        [
            null,
            new("", source, []),
            new("missing-source", null, []),
        ];

        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video, tracks);

        Assert.Empty(timeline.Tracks);
        Assert.Equal(
            [
                "audio.timeline.track.null",
                "audio.timeline.track.missing_id",
                "audio.timeline.track.missing_source",
            ],
            timeline.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.All(
            timeline.Diagnostics,
            diagnostic => Assert.Equal(PlanDiagnosticSeverity.Warning, diagnostic.Severity));
    }

    [Theory]
    [InlineData(0, 99, "audio.timeline.span.unknown_last_clip")]
    [InlineData(1, 0, "audio.timeline.span.reversed_clip_range")]
    public void Invalid_clip_range_anchors_warn_and_skip_the_span(
        int firstClipId,
        int lastClipId,
        string expectedCode)
    {
        VideoExecutionPlan video = Plan(Clip(0), Clip(1));
        AudioTimelinePlan timeline = AudioTimelinePlanCompiler.Compile(video,
        [
            Track("invalid-range", new AudioTrackSpanSpec(
                FirstClipId: firstClipId,
                LastClipId: lastClipId)),
        ]);

        Assert.Empty(Track(timeline, "invalid-range").Windows);
        Assert.Contains(timeline.Diagnostics, diagnostic =>
            diagnostic.Code == expectedCode
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Timeline_metadata_conflicts_are_warnings()
    {
        VideoExecutionPlan normal = Plan(Clip(0), Clip(1));
        VideoExecutionPlan duplicateClips = normal with
        {
            Clips = [normal.Clips[0], normal.Clips[0]],
        };
        VideoExecutionPlan invalidFps = normal with { FramesPerSecond = 0 };
        VideoExecutionPlan duplicateBoundaries = normal with
        {
            Boundaries = [normal.Boundaries[0], normal.Boundaries[0]],
        };

        AssertWarning(
            AudioTimelinePlanCompiler.Compile(duplicateClips),
            "audio.timeline.clip.duplicate_id");
        AssertWarning(
            AudioTimelinePlanCompiler.Compile(invalidFps),
            "audio.timeline.invalid_fps");
        AssertWarning(
            AudioTimelinePlanCompiler.Compile(duplicateBoundaries),
            "audio.timeline.boundary.duplicate_from_clip");
    }

    private static void AssertWarning(AudioTimelinePlan plan, string code) =>
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == code
                && diagnostic.Severity == PlanDiagnosticSeverity.Warning);
}
