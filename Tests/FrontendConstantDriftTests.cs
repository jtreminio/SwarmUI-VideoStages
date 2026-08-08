using System.Globalization;
using System.Text.RegularExpressions;
using VideoStages.Authoring;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class FrontendConstantDriftTests
{
    [Fact]
    public void Frontend_ic_lora_strength_bounds_match_the_backend()
    {
        Assert.Equal<double>(
            Loras.IcLoraStrengthMin,
            FrontendConstant("icLoraAuthoring.ts", "IC_LORA_STRENGTH_MIN"));
        Assert.Equal<double>(
            Loras.IcLoraStrengthMax,
            FrontendConstant("icLoraAuthoring.ts", "IC_LORA_STRENGTH_MAX"));
    }

    [Fact]
    public void Frontend_stage_ref_strength_default_matches_the_backend()
    {
        Assert.Equal(
            Constants.DefaultStageRefStrength,
            FrontendConstant("constants.ts", "STAGE_REF_STRENGTH_DEFAULT"));
    }

    [Fact]
    public void Frontend_audio_span_bounds_match_the_backend()
    {
        Assert.Equal(
            AuthoringTimeline.MinAudioLength,
            FrontendConstant("constants.ts", "AUDIO_SPAN_MIN_LENGTH"));
        Assert.Equal(
            AuthoringTimeline.MinAudioVolume,
            FrontendConstant("constants.ts", "AUDIO_SPAN_VOLUME_MIN"));
        Assert.Equal(
            AuthoringTimeline.MaxAudioVolume,
            FrontendConstant("constants.ts", "AUDIO_SPAN_VOLUME_MAX"));
    }

    /// <summary>The snap grid needs no assertion here: every case in dimension-snap-cases.json is
    /// grid-sensitive, so both languages red on it. No case reaches the clamp — max rawWidth is
    /// 1344 — and the two that do assert it spell 4096 by hand on each side.</summary>
    [Fact]
    public void Frontend_root_dimension_maximum_matches_the_backend()
    {
        Assert.Equal<double>(
            DimensionSnap.MaximumDimension,
            FrontendConstant("constants.ts", "ROOT_DIMENSION_MAX"));
    }

    /// <summary>Reads a number out of a hand-written frontend module. Parsing beats matching the
    /// rendered text, which reads two spellings of one number as drift: C# writes 0.00001 as
    /// 1E-05, and TypeScript may write 4096 as 4_096. Insisting on exactly one declaration is
    /// what makes a stale commented-out copy fail rather than mask a drifted live one — a block
    /// comment leaves its body at the start of a line, where this pattern still matches.</summary>
    private static double FrontendConstant(string module, string name)
    {
        MatchCollection declarations = Regex.Matches(
            RepoFiles.ReadFrontend(module),
            $"^export const {name} = (\\S+);$",
            RegexOptions.Multiline);
        Assert.True(
            declarations.Count == 1,
            $"{module} must declare {name} exactly once, not {declarations.Count} times.");
        return double.Parse(
            declarations[0].Groups[1].Value.Replace("_", ""),
            CultureInfo.InvariantCulture);
    }
}
