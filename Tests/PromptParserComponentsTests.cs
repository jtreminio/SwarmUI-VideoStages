using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class PromptParserComponentsTests
{
    [Fact]
    public void Tag_scanner_distinguishes_prose_complete_tags_and_unclosed_tags()
    {
        PromptTagScanner.Piece[] pieces = [.. PromptTagScanner.Scan("lead<tag>body<unfinished")];

        Assert.Equal(3, pieces.Length);
        Assert.True(pieces[0].IsLeadingText);
        Assert.Equal("lead", pieces[0].Text);
        Assert.True(pieces[1].HasTag);
        Assert.Equal("tag", pieces[1].Tag);
        Assert.Equal("body", pieces[1].Content);
        Assert.False(pieces[2].HasTag);
        Assert.Equal("unfinished", pieces[2].Text);
    }

    [Theory]
    [InlineData("0-1", 0, 1)]
    [InlineData("0.25-4.5", 0.25, 4.5)]
    public void Window_syntax_accepts_finite_invariant_ranges(string value, double expectedStart, double expectedEnd)
    {
        Assert.True(VideoClipPromptSyntax.TryParseWindow(value, out double start, out double end));
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
        Assert.False(VideoClipPromptSyntax.TryParseWindow(value, out _, out _));
    }

    [Fact]
    public void Prompt_text_canonicalizer_maps_only_the_selected_clip_stage()
    {
        string canonical = VideoClipTagCanonicalizer.CanonicalizeBrackets(
            "<videoclip[2,1]>selected<videoclip[2,0]>other",
            clipIndex: 2,
            clipStageIndexWithinClip: 1,
            globalCid: 100,
            clipCid: 101,
            stageCid: 102);

        Assert.Equal(
            "<videoclip//cid=102>selected<videoclip//cid=-1>other",
            canonical);
    }

    [Fact]
    public void Prompt_text_removal_stops_swallowing_at_the_next_section_starter()
    {
        string cleaned = VideoClipTagCanonicalizer.RemoveAllSections(
            "global<videoclip//cid=100>clip prose<video//cid=5>video prose");

        Assert.Equal("global<video//cid=5>video prose", cleaned);
    }
}
