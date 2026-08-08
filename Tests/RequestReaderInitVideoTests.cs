using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using VideoStages.Authoring;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>Reading a clip that refines uploaded footage: its duration guards, and the retake
/// window only such a clip can carry. FPS is left unset throughout, so the reader's 24 fps fallback
/// is what converts every authored second here.</summary>
[Collection("VideoStagesTests")]
public class RequestReaderInitVideoTests
{
    private static JObject SourcedClip(double duration, params JObject[] stages)
    {
        JObject clip = MakeClip(duration, stages);
        clip["initVideo"] = SourceVideo();
        return clip;
    }

    private static JObject MakeRetake(double startSeconds, double lengthSeconds, double? strength = null)
    {
        JObject retake = new()
        {
            ["startSeconds"] = startSeconds,
            ["lengthSeconds"] = lengthSeconds,
        };
        if (strength is not null)
        {
            retake["strength"] = strength.Value;
        }
        return retake;
    }

    private static T2IParamInput InputFor(JObject clip)
    {
        T2IParamInput input = new(null);
        SetVideoStagesConfig(input, new JArray(clip).ToString());
        return input;
    }

    [Fact]
    public void ReadClips_DropsNegativeDuration()
    {
        T2IParamInput input = InputFor(SourcedClip(-1, MakeStage("model-a")));

        ClipSpec parsed = Assert.Single(RequestReader.Read(input).Clips);

        Assert.Null(parsed.Frames);
        Assert.Contains(
            TypedWorkflowAssertions.RequestWarnings(input),
            warning => warning.Contains("duration must be finite and non-negative")
                && warning.Contains("ignoring it"));
    }

    [Fact]
    public void ReadClips_DropsDurationBeyondTheRepresentableFrameRange()
    {
        T2IParamInput input = InputFor(SourcedClip(int.MaxValue, MakeStage("model-a")));

        ClipSpec parsed = Assert.Single(RequestReader.Read(input).Clips);

        Assert.Null(parsed.Frames);
        Assert.Contains(
            TypedWorkflowAssertions.RequestWarnings(input),
            warning => warning.Contains("duration at 24 fps exceeds")
                && warning.Contains("was ignored"));
    }

    [Fact]
    public void ReadClips_Retake_ConvertsSecondsToFramesAtFpsAndAttachesToLastStage()
    {
        JObject clip = SourcedClip(3.0, MakeStage("model-a"), MakeStage("model-b"));
        clip["retake"] = MakeRetake(startSeconds: 1.0, lengthSeconds: 1.5, strength: 0.6);

        ClipSpec parsed = Assert.Single(RequestReader.Read(InputFor(clip)).Clips);

        Assert.Null(parsed.Stages[0].RetakeWindow);
        RetakeWindowSpec retake = parsed.Stages[^1].RetakeWindow;
        Assert.NotNull(retake);
        Assert.Equal(24, retake.StartFrame);
        Assert.Equal(36, retake.LengthFrames);
        Assert.Equal(0.6, retake.Strength, 6);
    }

    [Fact]
    public void ReadClips_Retake_ReachingClipEndExtendsToStructuralFrameCount()
    {
        // Model-grid normalization occurs later, after architecture resolution.
        JObject clip = SourcedClip(1.05, MakeStage("model-a"));
        clip["retake"] = MakeRetake(startSeconds: 0.5, lengthSeconds: 0.55);

        ClipSpec parsed = Assert.Single(RequestReader.Read(InputFor(clip)).Clips);

        RetakeWindowSpec retake = parsed.Stages[^1].RetakeWindow;
        Assert.NotNull(retake);
        Assert.Equal(12, retake.StartFrame);
        Assert.Equal(parsed.Frames!.Value - 12, retake.LengthFrames);
        Assert.Equal(27, parsed.Frames);
    }

    [Fact]
    public void ReadClips_Retake_DefaultsStrengthToOneWhenAbsent()
    {
        JObject clip = SourcedClip(3.0, MakeStage("model-a"));
        clip["retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 1.0);

        ClipSpec parsed = Assert.Single(RequestReader.Read(InputFor(clip)).Clips);

        RetakeWindowSpec retake = parsed.Stages[^1].RetakeWindow;
        Assert.NotNull(retake);
        Assert.Equal(0, retake.StartFrame);
        Assert.Equal(24, retake.LengthFrames);
        Assert.Equal(1.0, retake.Strength, 6);
    }

    [Fact]
    public void ReadClips_Retake_ClampsStrengthToUnitRange()
    {
        JObject clip = SourcedClip(3.0, MakeStage("model-a"));
        clip["retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 1.0, strength: 5.0);

        ClipSpec parsed = Assert.Single(RequestReader.Read(InputFor(clip)).Clips);

        Assert.Equal(1.0, parsed.Stages[^1].RetakeWindow.Strength, 6);
    }

    [Fact]
    public void ReadClips_Retake_NullWhenClipHasNoInitVideo()
    {
        JObject clip = MakeClip(3.0, MakeStage("model-a"));
        clip["retake"] = MakeRetake(startSeconds: 1.0, lengthSeconds: 2.0);

        ClipSpec parsed = Assert.Single(RequestReader.Read(InputFor(clip)).Clips);

        Assert.All(parsed.Stages, stage => Assert.Null(stage.RetakeWindow));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, -1.0)]
    [InlineData(-1.0, 2.0)]
    public void ReadClips_Retake_NullWhenInvalidWindow(double startSeconds, double lengthSeconds)
    {
        JObject clip = SourcedClip(3.0, MakeStage("model-a"));
        clip["retake"] = MakeRetake(startSeconds, lengthSeconds);

        ClipSpec parsed = Assert.Single(RequestReader.Read(InputFor(clip)).Clips);

        Assert.All(parsed.Stages, stage => Assert.Null(stage.RetakeWindow));
    }

    [Fact]
    public void ReadClips_Retake_NullWhenSubFrameLengthRoundsToZero()
    {
        JObject clip = SourcedClip(3.0, MakeStage("model-a"));
        // 0.01s at 24 fps => round(0.24) = 0 frames => disabled.
        clip["retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 0.01);

        ClipSpec parsed = Assert.Single(RequestReader.Read(InputFor(clip)).Clips);

        Assert.Null(parsed.Stages[^1].RetakeWindow);
    }

    [Fact]
    public void ReadClips_Retake_AbsentLeavesStagesUntouched()
    {
        ClipSpec parsed = Assert.Single(
            RequestReader.Read(InputFor(SourcedClip(3.0, MakeStage("model-a")))).Clips);

        Assert.Null(parsed.Stages[^1].RetakeWindow);
    }
}
