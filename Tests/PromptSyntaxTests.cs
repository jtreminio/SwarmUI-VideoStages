using VideoStages.Authoring;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class PromptSyntaxTests
{
    [Theory]
    [InlineData("0-1", 0, 1)]
    [InlineData("0.25-4.5", 0.25, 4.5)]
    public void Window_syntax_accepts_finite_invariant_ranges(string value, double expectedStart, double expectedEnd)
    {
        Assert.True(PromptSyntax.TryParseWindow(value, out double start, out double end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData("1-1")]
    [InlineData("2-1")]
    [InlineData("0-Infinity")]
    [InlineData("0-1,skip")]
    public void Window_syntax_rejects_non_increasing_non_finite_and_decorated_ranges(string value)
    {
        Assert.False(PromptSyntax.TryParseWindow(value, out _, out _));
    }

    /// <summary>Every authored override value passes through this; the characters it strips are the
    /// ones markers are built from, so a value that kept them could be re-read as a marker.</summary>
    [Theory]
    [InlineData(null, "")]
    [InlineData("<videoclip//cid=58823>", "videoclip58823")]
    [InlineData("<videoclip:w|0|1|2//cid=-1>", "videoclip:w012-1")]
    [InlineData("  first\nsecond\r  ", "first second")]
    public void Override_text_is_stripped_of_the_characters_markers_are_built_from(
        string value,
        string expected)
    {
        Assert.Equal(expected, PromptSyntax.SanitizeOverrideText(value));
    }
}
