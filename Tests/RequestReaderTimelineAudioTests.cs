using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>Reading the root audio tracks: every track's spans flatten into one executable list,
/// each span keeping its own source window, volume and clip projection.</summary>
[Collection("VideoStagesTests")]
public class RequestReaderTimelineAudioTests
{
    private static TimelineSpec ReadWithTracks(double clipSeconds, params JObject[] tracks)
    {
        JObject clip = MakeClip(clipSeconds, MakeStage("ltx-2"));
        clip["id"] = "clip-a";
        JObject root = new()
        {
            ["clips"] = new JArray(clip),
            ["audioTracks"] = new JArray(tracks.Cast<object>().ToArray()),
        };
        T2IParamInput input = new(null);
        SetVideoStagesConfig(input, root.ToString(Formatting.None));
        return RequestReader.Read(input);
    }

    private static JObject MakeAudioTrack(string id, params JObject[] spans) =>
        AudioTrack(id, volume: 0.5, fileName: "score.wav", spans);

    [Fact]
    public void Read_RootTimelineAudioSpans_PreservesExecutableSourceWindowAndVolume()
    {
        JObject span = AudioSpan(
            timelineStartSeconds: 1.5,
            timelineLengthSeconds: 2.5,
            sourceStartSeconds: 4);
        span["projection"] = new JObject
        {
            ["firstClipId"] = "clip-a",
            ["lastClipId"] = "clip-a",
            ["clipStartOffsetSeconds"] = 1.5,
            ["clipEndOffsetSeconds"] = 4,
        };

        TimelineAudioSpanSpec parsedSpan = Assert.Single(
            ReadWithTracks(
                4,
                AudioTrack("track-dialogue", volume: 0.75, fileName: "dialogue.wav", span))
                .TimelineAudioSpans);

        Assert.Equal("track-dialogue", parsedSpan.Id);
        Assert.Equal("dialogue.wav", parsedSpan.Source.FileName);
        Assert.Equal(1.5, parsedSpan.TimelineStartSeconds);
        Assert.Equal(2.5, parsedSpan.LengthSeconds);
        Assert.Equal(4, parsedSpan.SourceStartSeconds);
        Assert.Equal(0.75, parsedSpan.Volume);
        Assert.Equal(0, parsedSpan.FirstClipId);
        Assert.Equal(0, parsedSpan.LastClipId);
        Assert.Equal(1.5, parsedSpan.FirstClipOffsetSeconds);
        Assert.Equal(4, parsedSpan.LastClipOffsetSeconds);
    }

    [Fact]
    public void Read_RootTimelineAudioSpans_MultiSpanTrackMatchesSplitSingleSpanLanes()
    {
        IReadOnlyList<TimelineAudioSpanSpec> combined = ReadWithTracks(
            8,
            MakeAudioTrack(
                "track-multi",
                AudioSpan(0, 1, 0),
                AudioSpan(3, 2, 5))).TimelineAudioSpans;
        IReadOnlyList<TimelineAudioSpanSpec> split = ReadWithTracks(
            8,
            MakeAudioTrack("track-multi:0", AudioSpan(0, 1, 0)),
            MakeAudioTrack("track-multi:1", AudioSpan(3, 2, 5))).TimelineAudioSpans;

        Assert.Equal(2, combined.Count);
        Assert.Equal(
            ["track-multi:0", "track-multi:1"],
            combined.Select(span => span.Id).ToArray());
        Assert.Equal(
            combined.Select(span => (
                span.Id,
                span.TimelineStartSeconds,
                span.LengthSeconds,
                span.SourceStartSeconds,
                span.Volume)).ToArray(),
            split.Select(span => (
                span.Id,
                span.TimelineStartSeconds,
                span.LengthSeconds,
                span.SourceStartSeconds,
                span.Volume)).ToArray());
    }
}
