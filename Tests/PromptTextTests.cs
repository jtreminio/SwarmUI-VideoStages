using SwarmUI.Text2Image;
using VideoStages.Authoring;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class PromptTextTests
{
    [Fact]
    public void Videoclip_processed_cid_stage_section_extracts_only_for_matching_flat_stage()
    {
        int stage0Sid = VideoStagesExtension.SectionIdForStage(0);
        string prompt = $"global preamble <videoclip//cid={stage0Sid}>exclusive-stage-zero";

        Assert.Equal(
            "exclusive-stage-zero",
            PromptText.SelectMatchingSections(prompt, 0, 0, 0).Trim());
        Assert.Contains(
            "global preamble",
            PromptText.SelectMatchingSections(prompt, 0, 1, 1).Trim());
        Assert.DoesNotContain(
            "exclusive-stage-zero",
            PromptText.SelectMatchingSections(prompt, 0, 1, 1));
    }

    [Fact]
    public void Videoclip_raw_clip_stage_predicate_matches_comma_bracket_syntax()
    {
        string prompt = "global <videoclip[0,0]>tiered";
        Assert.Equal("tiered", PromptText.SelectMatchingSections(prompt, 0, 0, 0).Trim());
        Assert.DoesNotContain("tiered", PromptText.SelectMatchingSections(prompt, 0, 1, 1));
        Assert.True(PromptText.HasAnySectionForClip(prompt, 0));
    }

    [Fact]
    public void Raw_stage_sections_select_only_the_requested_stage()
    {
        string prompt = "<videoclip[2,1]>selected<videoclip[2,0]>other";

        Assert.Equal(
            "selected",
            PromptText.SelectMatchingSections(prompt, 2, 5, 1));
    }

    [Theory]
    [InlineData("video", "video prose")]
    [InlineData("pixeldecoder", "decoder prose")]
    public void Unselected_clip_text_stops_at_the_next_section(string section, string prose)
    {
        string prompt = $"global<videoclip//cid=-1>clip prose<{section}//cid=5>{prose}";

        Assert.Equal(
            $"global<{section}//cid=5>{prose}",
            PromptText.SelectMatchingSections(prompt, 0, null, null));
    }

    [Fact]
    public void Videoclip_tag_only_section_falls_back_to_video_section_before_global()
    {
        int videoclipCid = Constants.SectionID_VideoClip;
        int videoCid = T2IParamInput.SectionID_Video;
        string processedPrompt =
            $"Main prompt<video//cid={videoCid}>Video Prompt<videoclip//cid={videoclipCid}>";
        string originalPrompt =
            "Main prompt<video>Video Prompt<videoclip><lora:LTX-2/ltx-2.3-22b-distilled-lora-384-1.1>";

        Assert.Equal(
            "Video Prompt",
            PromptText.ResolveForTarget(processedPrompt, originalPrompt, clipIndex: 0).Trim());
    }
}
