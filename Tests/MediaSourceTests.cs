using System.Runtime.CompilerServices;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MediaSourceTests
{
    [Theory]
    [InlineData("ControlNet 1", 0)]
    [InlineData(" controlnet2 ", 1)]
    [InlineData("CONTROL NET 3", 2)]
    public void ControlNet_source_round_trips(string source, int expectedIndex)
    {
        Assert.True(MediaSource.TryParseControlNetIndex(source, out int index));
        Assert.Equal(expectedIndex, index);
        Assert.Equal($"ControlNet {expectedIndex + 1}", MediaSource.FormatControlNet(index));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ControlNet")]
    [InlineData("ControlNet 0")]
    [InlineData("ControlNet 4")]
    public void Invalid_ControlNet_source_is_rejected(string source)
    {
        Assert.False(MediaSource.TryParseControlNetIndex(source, out int index));
        Assert.Equal(-1, index);
    }

    [Theory]
    [InlineData("audio0", 0)]
    [InlineData(" AUDIO 7 ", 7)]
    public void AceStepFun_source_round_trips(string source, int expectedIndex)
    {
        Assert.True(MediaSource.TryParseAceStepFunIndex(source, out int index));
        Assert.Equal(expectedIndex, index);
        Assert.Equal($"audio{expectedIndex}", MediaSource.FormatAceStepFun(index));
    }

    [Theory]
    [InlineData("edit0", 0)]
    [InlineData(" edit 7 ", 7)]
    [InlineData("EDIT12", 12)]
    public void Base2Edit_source_round_trips(string source, int expectedIndex)
    {
        Assert.True(MediaSource.TryParseBase2EditIndex(source, out int index));
        Assert.Equal(expectedIndex, index);
        Assert.Equal($"edit{expectedIndex}", MediaSource.FormatBase2Edit(index));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("audio")]
    [InlineData("audio-1")]
    public void Invalid_AceStepFun_source_is_rejected(string source)
    {
        Assert.False(MediaSource.TryParseAceStepFunIndex(source, out int index));
        Assert.Equal(-1, index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("edit")]
    [InlineData("edit-1")]
    [InlineData("Edit Stage 0")]
    public void Invalid_Base2Edit_source_is_rejected(string source)
    {
        Assert.False(MediaSource.TryParseBase2EditIndex(source, out int index));
        Assert.Equal(-1, index);
    }

    [Fact]
    public void Negative_index_cannot_be_formatted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaSource.FormatControlNet(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaSource.FormatAceStepFun(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaSource.FormatBase2Edit(-1));
    }

    [Fact]
    public void Generated_typescript_media_source_names_are_current()
    {
        string committedPath = Path.GetFullPath(
            Path.Combine(TestSourceDirectory(), "..", "frontend", "generatedMediaSource.ts"));

        Assert.Equal(
            MediaSource.RenderGeneratedTypeScript(),
            File.ReadAllText(committedPath));
    }

    private static string TestSourceDirectory(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}
