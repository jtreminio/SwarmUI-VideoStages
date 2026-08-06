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

}
