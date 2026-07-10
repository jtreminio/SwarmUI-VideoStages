using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Covers the value-carrying members of the <c>&lt;videoclip&gt;</c> / <c>&lt;videostages&gt;</c> tag family —
/// prompt-relay windows (seconds), scalar overrides, and the prose-ownership rules that keep override prose in its
/// enclosing section, swallow invalid section/window prose, and discard the legacy <c>&lt;videostages&gt;{json}</c> blob.
/// Section-prose extraction and lora scoping are covered by <see cref="StageFlowTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public class VideoClipPromptTagsTests
{
    /// <summary>Runs the authoring prompt through the basic processors and returns (normalized markers, input).</summary>
    private static (string Prompt, T2IParamInput Input) Normalize(string authoringPrompt)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, authoringPrompt);
        input.ApplyLateSpecialLogic();
        return (input.Get(T2IParamTypes.Prompt, ""), input);
    }

    private static PromptParser.VideoStageTagData Tags(string authoringPrompt)
    {
        (string prompt, T2IParamInput input) = Normalize(authoringPrompt);
        return PromptParser.ExtractTagData(prompt, input);
    }

    // --- Windows (seconds) ---

    [Fact]
    public void Clip_window_tag_parses_clip_level_window()
    {
        PromptParser.VideoStageTagData data = Tags("<videoclip[0]:1-2>win");
        PromptWindowSpec window = Assert.Single(data.ClipWindows[0]);
        Assert.Equal("win", window.Prompt);
        Assert.Equal(1.0, window.Start);
        Assert.Equal(1.0, window.Duration);
    }

    [Fact]
    public void Stage_scoped_window_is_invalid_and_swallows_its_prose()
    {
        // Windows are clip-level only; a [N,M] range value is an invalid tag -> no window, prose swallowed.
        (string prompt, T2IParamInput input) = Normalize("<videoclip[0,1]:5-10>oops");
        PromptParser.VideoStageTagData data = PromptParser.ExtractTagData(prompt, input);
        Assert.Empty(data.ClipWindows);
        // "oops" is owned by the unmatched swallow-marker, so it never lands in the global/base prose.
        Assert.Equal("", new PromptRegion(prompt).GlobalPrompt.Trim());
    }

    [Fact]
    public void Window_with_end_before_start_is_not_a_window()
    {
        Assert.Empty(Tags("<videoclip[0]:5-2>x").ClipWindows);
    }

    [Fact]
    public void Window_with_comma_value_is_not_a_window()
    {
        // A comma inside a window value (formerly the ,skip flag) now makes it an invalid window.
        Assert.Empty(Tags("<videoclip[0]:0-1,skip>x").ClipWindows);
    }

    [Theory]
    [InlineData("<videoclip[0]:0-Infinity>x")]
    [InlineData("<videoclip[0]:0-1e400>x")]
    public void Window_with_non_finite_bound_is_not_a_window(string authoringPrompt)
    {
        // NumberStyles.Float parses "Infinity" and overflows huge exponents (1e400) to +inf; such a
        // bound must not build a window (a window with an infinite duration is meaningless), matching
        // the frontend's Number.isFinite gate.
        Assert.Empty(Tags(authoringPrompt).ClipWindows);
    }

    [Fact]
    public void Window_with_finite_bounds_is_a_window()
    {
        // Sanity: the non-finite guard does not reject ordinary finite windows.
        PromptWindowSpec window = Assert.Single(Tags("<videoclip[0]:0-5>x").ClipWindows[0]);
        Assert.Equal(0.0, window.Start);
        Assert.Equal(5.0, window.Duration);
    }

    // --- Overrides (comma grammar) ---

    [Fact]
    public void Clip_scalar_override_parses()
    {
        (string field, string value) = Assert.Single(Tags("<videoclip[0,duration]:5.5>").ClipOverrides[0]);
        Assert.Equal("duration", field);
        Assert.Equal("5.5", value);
    }

    [Fact]
    public void Stage_scalar_override_parses()
    {
        (string field, string value) = Assert.Single(Tags("<videoclip[0,1,control]:0.5>").StageOverrides[(0, 1)]);
        Assert.Equal("control", field);
        Assert.Equal("0.5", value);
    }

    [Fact]
    public void Top_level_override_parses()
    {
        (string field, string value) = Assert.Single(Tags("<videostages[width]:1280>").TopLevelOverrides);
        Assert.Equal("width", field);
        Assert.Equal("1280", value);
    }

    [Fact]
    public void Unknown_top_level_override_field_is_dropped()
    {
        Assert.Empty(Tags("<videostages[bogus]:1280>").TopLevelOverrides);
    }

    // --- Prose ownership ---

    [Fact]
    public void Override_tag_leaves_trailing_prose_in_the_leading_global_area()
    {
        (string prompt, _) = Normalize("A cat <videoclip[0,duration]:20> playing");
        // The override tag vanishes; its surrounding prose stays put (no marker owns it).
        Assert.Equal("A cat  playing", new PromptRegion(prompt).GlobalPrompt.Trim());
        Assert.DoesNotContain("videoclip", prompt);
        (string field, string value) = Assert.Single(Tags("A cat <videoclip[0,duration]:20> playing").ClipOverrides[0]);
        Assert.Equal("duration", field);
        Assert.Equal("20", value);
    }

    [Fact]
    public void Invalid_window_swallows_its_own_prose_without_polluting_the_prior_window()
    {
        // Second tag is an invalid window (end == start): its "drop" prose is swallowed, not bled into window one.
        PromptParser.VideoStageTagData data = Tags("<videoclip[0]:1-2>keep <videoclip[0]:1-1>drop");
        PromptWindowSpec window = Assert.Single(data.ClipWindows[0]);
        Assert.Equal("keep", window.Prompt);
    }

    [Fact]
    public void Legacy_bare_videostages_blob_is_discarded()
    {
        (string prompt, _) = Normalize("real prompt <videostages>{\"clips\":[]} tail");
        // The stale JSON blob (and its trailing text) is swallowed by an unmatched videoclip section.
        Assert.Equal("real prompt", new PromptRegion(prompt).GlobalPrompt.Trim());
        Assert.DoesNotContain("clips", prompt.Split('>')[0]);
    }
}
