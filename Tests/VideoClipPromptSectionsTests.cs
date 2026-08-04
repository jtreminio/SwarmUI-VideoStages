using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Section-prose extraction for the <c>&lt;videoclip&gt;</c> tag family: the comma bracket grammar and
/// the fallback chain, both of which are pure string work over a processed prompt. The LoRA
/// confinement those sections drive is generated end-to-end in
/// <see cref="ArchitectureContractGraphTests"/>.
/// </summary>
public partial class StageFlowTests
{
    [Fact]
    public void Videoclip_processed_cid_stage_section_extracts_only_for_matching_flat_stage()
    {
        int stage0Sid = VideoStagesExtension.SectionIdForStage(0);
        string prompt = $"global preamble <videoclip//cid={stage0Sid}>exclusive-stage-zero";

        Assert.Equal(
            "exclusive-stage-zero",
            VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 0, 0).Trim());
        Assert.Contains(
            "global preamble",
            VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 1, 1).Trim());
        Assert.DoesNotContain(
            "exclusive-stage-zero",
            VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 1, 1));
    }

    [Fact]
    public void Videoclip_raw_clip_stage_predicate_matches_comma_bracket_syntax()
    {
        string prompt = "global <videoclip[0,0]>tiered";
        Assert.Equal("tiered", VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 0, 0).Trim());
        Assert.DoesNotContain("tiered", VideoClipPromptText.ExtractWithoutReferences(prompt, 0, 1, 1));
        Assert.True(VideoClipPromptText.HasAnySectionForClip(prompt, 0));
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
            PromptParser.ExtractPrompt(processedPrompt, originalPrompt, clipIndex: 0).Trim());
    }
}
