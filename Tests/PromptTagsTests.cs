using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class PromptTagsTests
{
    private static (string Prompt, T2IParamInput Input) Normalize(string authoringPrompt, string videoStagesJson = null)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, authoringPrompt);
        if (videoStagesJson is not null)
        {
            Fixtures.SetVideoStagesConfig(input, videoStagesJson);
        }
        input.ApplyLateSpecialLogic();
        return (input.Get(T2IParamTypes.Prompt, ""), input);
    }

    private static PromptTags.Directives ReadDirectives(string authoringPrompt)
    {
        (string prompt, T2IParamInput input) = Normalize(authoringPrompt);
        return PromptTags.Read(prompt, input);
    }

    // --- Windows (seconds) ---

    [Fact]
    public void Clip_window_tag_parses_clip_level_window()
    {
        PromptTags.Directives directives = ReadDirectives("<videoclip[0]:1-2>win");
        PromptWindowSpec window = Assert.Single(directives.ClipWindows[0]);
        Assert.Equal("win", window.Prompt);
        Assert.Equal(1.0, window.Start);
        Assert.Equal(1.0, window.Duration);
    }

    [Fact]
    public void Stage_scoped_window_is_invalid_and_swallows_its_prose()
    {
        (string prompt, T2IParamInput input) = Normalize("<videoclip[0,1]:5-10>oops");
        PromptTags.Directives directives = PromptTags.Read(prompt, input);
        Assert.Empty(directives.ClipWindows);
        Assert.Equal("", new PromptRegion(prompt).GlobalPrompt.Trim());
    }

    [Fact]
    public void Window_with_end_before_start_is_not_a_window()
    {
        Assert.Empty(ReadDirectives("<videoclip[0]:5-2>x").ClipWindows);
    }

    [Fact]
    public void Window_with_comma_value_is_not_a_window()
    {
        Assert.Empty(ReadDirectives("<videoclip[0]:0-1,skip>x").ClipWindows);
    }

    [Theory]
    [InlineData("<videoclip[0]:0-Infinity>x")]
    [InlineData("<videoclip[0]:0-1e400>x")]
    public void Window_with_non_finite_bound_is_not_a_window(string authoringPrompt)
    {
        // NumberStyles.Float accepts these values, but the frontend's Number.isFinite gate does not.
        Assert.Empty(ReadDirectives(authoringPrompt).ClipWindows);
    }

    [Fact]
    public void Window_with_finite_bounds_is_a_window()
    {
        PromptWindowSpec window = Assert.Single(ReadDirectives("<videoclip[0]:0-5>x").ClipWindows[0]);
        Assert.Equal(0.0, window.Start);
        Assert.Equal(5.0, window.Duration);
    }

    // --- Overrides (comma grammar) ---

    [Fact]
    public void Clip_scalar_override_parses()
    {
        (string field, string value) = Assert.Single(ReadDirectives("<videoclip[0,duration]:5.5>").ClipOverrides[0]);
        Assert.Equal("duration", field);
        Assert.Equal("5.5", value);
    }

    [Fact]
    public void Stage_scalar_override_parses()
    {
        (string field, string value) = Assert.Single(ReadDirectives("<videoclip[0,1,control]:0.5>").StageOverrides[(0, 1)]);
        Assert.Equal("control", field);
        Assert.Equal("0.5", value);
    }

    [Fact]
    public void Top_level_override_parses()
    {
        (string field, string value) = Assert.Single(ReadDirectives("<videostages[width]:1280>").TopLevelOverrides);
        Assert.Equal("width", field);
        Assert.Equal("1280", value);
    }

    [Fact]
    public void Repeated_extraction_copies_typed_overrides_once()
    {
        (string prompt, T2IParamInput input) = Normalize(
            "<videoclip[0,duration]:5.5>");

        PromptTags.Directives first = PromptTags.Read(prompt, input);
        PromptTags.Directives second = PromptTags.Read(prompt, input);
        PromptTags.Directives cloned = PromptTags.Read(
            prompt,
            input.Clone());

        PromptTags.Directives stored =
            Assert.IsType<PromptTags.Directives>(
                input.ExtraMeta[PromptTags.OverridesKey]);
        Assert.NotSame(stored, first);
        Assert.NotSame(stored, second);
        Assert.NotSame(stored, cloned);
        Assert.NotSame(first, second);
        Assert.NotSame(first, cloned);
        Assert.NotSame(second, cloned);
        Assert.Single(first.ClipOverrides[0]);
        Assert.Single(second.ClipOverrides[0]);
        Assert.Single(cloned.ClipOverrides[0]);
        first.ClipOverrides[0].Add(("duration", "9"));
        Assert.Single(stored.ClipOverrides[0]);
        Assert.Single(second.ClipOverrides[0]);
        Assert.Single(cloned.ClipOverrides[0]);
    }

    [Fact]
    public void Restored_override_metadata_does_not_block_new_tags()
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.ExtraMeta[PromptTags.OverridesKey] = "serialized metadata";
        input.Set(T2IParamTypes.Prompt, "<videoclip[0,duration]:5.5>");

        input.ApplyLateSpecialLogic();

        PromptTags.Directives stored =
            Assert.IsType<PromptTags.Directives>(
                input.ExtraMeta[PromptTags.OverridesKey]);
        Assert.Single(stored.ClipOverrides[0]);
    }

    [Fact]
    public void Unknown_top_level_override_field_is_dropped()
    {
        Assert.Empty(ReadDirectives("<videostages[bogus]:1280>").TopLevelOverrides);
    }

    // --- Prose ownership ---

    [Fact]
    public void Override_tag_leaves_trailing_prose_in_the_leading_global_area()
    {
        (string prompt, _) = Normalize("A cat <videoclip[0,duration]:20> playing");
        Assert.Equal("A cat  playing", new PromptRegion(prompt).GlobalPrompt.Trim());
        Assert.DoesNotContain("videoclip", prompt);
        (string field, string value) = Assert.Single(ReadDirectives("A cat <videoclip[0,duration]:20> playing").ClipOverrides[0]);
        Assert.Equal("duration", field);
        Assert.Equal("20", value);
    }

    [Fact]
    public void Invalid_window_swallows_its_own_prose_without_polluting_the_prior_window()
    {
        PromptTags.Directives directives = ReadDirectives("<videoclip[0]:1-2>keep <videoclip[0]:1-1>drop");
        PromptWindowSpec window = Assert.Single(directives.ClipWindows[0]);
        Assert.Equal("keep", window.Prompt);
    }

    [Fact]
    public void Legacy_bare_videostages_blob_is_discarded()
    {
        (string prompt, _) = Normalize("real prompt <videostages>{\"clips\":[]} tail");
        Assert.Equal("real prompt", new PromptRegion(prompt).GlobalPrompt.Trim());
        Assert.DoesNotContain("clips", prompt.Split('>')[0]);
    }

    // --- Metadata restore (processed markers -> authored syntax) ---

    [Fact]
    public void Metadata_restore_round_trips_window_tags()
    {
        string authored =
            "a young woman, smiling at the viewer<videoclip[0]:0.5-4>she says \"hi\"<videoclip[0]:4-9.5>she giggles";
        (string processed, _) = Normalize(authored);
        Assert.NotEqual(authored, processed);
        Assert.Equal(authored, PromptSyntax.RestoreForMetadata(processed));
    }

    [Fact]
    public void Metadata_restore_round_trips_clip_section_tags()
    {
        string authored = "base prose<videoclip[1]>clip prose";
        (string processed, _) = Normalize(authored);
        Assert.Equal(authored, PromptSyntax.RestoreForMetadata(processed));
    }

    [Fact]
    public void Metadata_restore_round_trips_stage_section_tags()
    {
        string authored = "base prose<videoclip[0,0]>stage prose";
        string videoStagesJson = Fixtures.MakeDocument(
            Fixtures.MakeClip(Fixtures.MakeStage("unit-stage"))).ToString();
        (string processed, _) = Normalize(authored, videoStagesJson);

        Assert.Contains($"//cid={VideoStagesExtension.SectionIdForStage(0)}", processed);
        Assert.Equal(authored, PromptSyntax.RestoreForMetadata(processed));
    }

    [Fact]
    public void Metadata_restore_round_trips_bare_videoclip_tag()
    {
        string authored = "base prose<videoclip>shared prose";
        (string processed, _) = Normalize(authored);
        Assert.Equal(authored, PromptSyntax.RestoreForMetadata(processed));
    }

    [Fact]
    public void Metadata_restore_leaves_unmatched_markers_untouched()
    {
        string marker = $"<videoclip//cid={Constants.SectionID_VideoClipUnmatched}>";
        Assert.Equal(marker, PromptSyntax.RestoreForMetadata(marker));
    }
}
