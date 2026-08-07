using VideoStages.Authoring;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class TimelineAudioSpanCompilerTests
{
    private const int Fps = 24;
    private const int Frames = 49;

    [Fact]
    public void Boundary_trims_partition_segments_and_advance_source_time()
    {
        VideoExecutionPlan plan = Plan(
            Clip(0, boundary: Constants.BoundaryOutContinue, overlap: 8),
            Clip(1, boundary: Constants.BoundaryOutCrossfade, overlap: 16),
            Clip(2));

        TimelineAudioSpanCompilation result = TimelineAudioSpanCompiler.Compile(
            plan.FramesPerSecond,
            plan.Clips,
            plan.Boundaries,
            [Segment("score", 0, 20)]);

        AudioSpanPlan[] items = result.Clips
            .Select(clip => Assert.Single(clip.Audio.Spans))
            .ToArray();
        double[] expectedLengths = [48d / Fps, 33d / Fps, 49d / Fps];
        double[] expectedStarts = [0d, 48d / Fps, 81d / Fps];
        for (int i = 0; i < items.Length; i++)
        {
            Assert.Equal(expectedLengths[i], items[i].LengthSeconds, 8);
            Assert.Equal(expectedStarts[i], items[i].TrimStartSeconds, 8);
        }
    }

    [Fact]
    public void Unknown_clip_duration_rejects_the_segment_atomically()
    {
        VideoExecutionPlan plan = Plan(Clip(0), Clip(1, frames: null));

        TimelineAudioSpanCompilation result = TimelineAudioSpanCompiler.Compile(
            plan.FramesPerSecond,
            plan.Clips,
            plan.Boundaries,
            [Segment("early", 0, 1)]);

        Assert.All(result.Clips, clip => Assert.Empty(clip.Audio.Spans));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.span.unresolved_clip_timing");
    }

    [Fact]
    public void Duplicate_segment_id_keeps_the_first_usable_segment()
    {
        VideoExecutionPlan plan = Plan(Clip(0));

        TimelineAudioSpanCompilation result = TimelineAudioSpanCompiler.Compile(
            plan.FramesPerSecond,
            plan.Clips,
            plan.Boundaries,
            [
                Segment("duplicate", 0, 1, sourceStart: 2),
                Segment("duplicate", 0, 1, sourceStart: 9),
            ]);

        Assert.Equal(2, Assert.Single(result.Clips).Audio.Spans[0].TrimStartSeconds);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.track.duplicate_id");
    }

    [Fact]
    public void Valid_AceStepFun_source_wins_over_upload()
    {
        VideoExecutionPlan plan = Plan(Clip(0));
        TimelineAudioSpanSpec span = Segment("voice", 0, 1) with
        {
            AceStepFunSource = "audio3",
        };

        TimelineAudioSpanCompilation result = TimelineAudioSpanCompiler.Compile(
            plan.FramesPerSecond,
            plan.Clips,
            plan.Boundaries,
            [span]);

        AudioSpanPlan item = Assert.Single(
            Assert.Single(result.Clips).Audio.Spans);
        Assert.Equal(AudioSourceKind.AceStepFun, item.SourceKind);
        Assert.Equal(3, item.AceStepFunTrack);
        Assert.Null(item.UploadedMedia);
    }

    [Fact]
    public void Invalid_AceStepFun_source_falls_back_to_upload()
    {
        VideoExecutionPlan plan = Plan(Clip(0));
        TimelineAudioSpanSpec span = Segment("voice", 0, 1) with
        {
            AceStepFunSource = "not-a-track",
        };

        TimelineAudioSpanCompilation result = TimelineAudioSpanCompiler.Compile(
            plan.FramesPerSecond,
            plan.Clips,
            plan.Boundaries,
            [span]);

        AudioSpanPlan item = Assert.Single(
            Assert.Single(result.Clips).Audio.Spans);
        Assert.Equal(AudioSourceKind.Upload, item.SourceKind);
        Assert.NotNull(item.UploadedMedia);
    }

    [Fact]
    public void Empty_first_segment_still_claims_its_duplicate_id()
    {
        VideoExecutionPlan plan = Plan(Clip(0));

        TimelineAudioSpanCompilation result = TimelineAudioSpanCompiler.Compile(
            plan.FramesPerSecond,
            plan.Clips,
            plan.Boundaries,
            [
                Segment("duplicate", 100, 1),
                Segment("duplicate", 0, 1),
            ]);

        Assert.Empty(Assert.Single(result.Clips).Audio.Spans);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.span.empty_after_projection");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "audio.timeline.track.duplicate_id");
    }

    [Fact]
    public void Timeline_metadata_conflicts_are_warnings()
    {
        VideoExecutionPlan plan = Plan(Clip(0), Clip(1));
        AssertWarning(
            TimelineAudioSpanCompiler.Compile(
                plan.FramesPerSecond,
                [plan.Clips[0], plan.Clips[0]],
                plan.Boundaries,
                []),
            "audio.timeline.clip.duplicate_id");
        AssertWarning(
            TimelineAudioSpanCompiler.Compile(
                0,
                plan.Clips,
                plan.Boundaries,
                []),
            "audio.timeline.invalid_fps");
        AssertWarning(
            TimelineAudioSpanCompiler.Compile(
                plan.FramesPerSecond,
                plan.Clips,
                [plan.Boundaries[0], plan.Boundaries[0]],
                []),
            "audio.timeline.boundary.duplicate_from_clip");
    }

    private static void AssertWarning(
        TimelineAudioSpanCompilation result,
        string code) => Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == code && diagnostic.Severity == PlanDiagnosticSeverity.Warning);

    private static TimelineAudioSpanSpec Segment(
        string id,
        double start,
        double length,
        double sourceStart = 0) => new(
            id,
            new UploadedMediaSpec("data:audio/wav;base64,QUJD", $"{id}.wav"),
            null,
            start,
            sourceStart,
            length);

    private static VideoExecutionPlan Plan(params ClipSpec[] clips) =>
        TestPlanCompiler.Compile(new TimelineSpec(512, 512, Fps, false, clips));

    private static ClipSpec Clip(
        int id,
        int? frames = Frames,
        string boundary = Constants.BoundaryOutCut,
        int overlap = 8) => new(
            id,
            frames,
            MediaSource.Native,
            [],
            false,
            false,
            false,
            false,
            null,
            [],
            [new(id, 1, 1, "pixel-lanczos", "ltx-2", 8, 1, "euler", "normal", "Generated")],
            BoundaryOut: boundary,
            BoundaryOutOverlap: overlap);
}
