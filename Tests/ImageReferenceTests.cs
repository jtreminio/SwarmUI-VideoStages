using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ImageReferenceTests
{
    // Unlike Fixtures.MakeStage, this helper leaves imageReference optional.
    private static JObject MakeStage(string model, string imageReference = null) =>
        new()
        {
            ["control"] = 1.0,
            ["upscale"] = 1.0,
            ["upscaleMethod"] = "pixel-lanczos",
            ["model"] = model,
            ["steps"] = 12,
            ["cfgScale"] = 5.0,
            ["sampler"] = "euler",
            ["scheduler"] = "normal",
            ["imageReference"] = imageReference
        };

    private static T2IParamInput BuildInput(string stagesJson)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "unit test prompt");
        SetVideoStagesConfig(input, stagesJson);
        return input;
    }

    private static List<StageSpec> ParseStages(T2IParamInput input)
    {
        return [.. RequestReader.Read(input).Clips.SelectMany(c => c.Stages)];
    }

    private static List<StageSpec> ParseStages(string stagesJson)
    {
        return ParseStages(BuildInput(stagesJson));
    }

    [Fact]
    public void Stage_zero_defaults_to_generated_when_no_image_reference_is_specified()
    {
        List<StageSpec> stages = ParseStages(JsonSingleClipStages(
            MakeStage("UnitTest_Video.safetensors")));

        Assert.Single(stages);
        Assert.Equal("Generated", stages[0].ImageReference);
    }

    [Fact]
    public void Forward_stage_image_reference_warns_and_uses_the_default()
    {
        T2IParamInput input = BuildInput(JsonSingleClipStages(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors", "Stage9")));

        List<StageSpec> stages = ParseStages(input);

        Assert.Equal("PreviousStage", stages[1].ImageReference);
        Assert.Contains(
            Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("invalid ImageReference 'Stage9'")
                && warning.Contains("strictly previous stage")
                && warning.Contains("stage 1"));
    }

    [Fact]
    public void Invalid_image_reference_warns_and_uses_the_default()
    {
        T2IParamInput input = BuildInput(JsonSingleClipStages(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors", "NotARealReference")));

        List<StageSpec> stages = ParseStages(input);

        Assert.Equal("PreviousStage", stages[1].ImageReference);
        Assert.Contains(
            Assert.IsType<List<string>>(input.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("invalid ImageReference 'NotARealReference'")
                && warning.Contains("Using 'PreviousStage'"));
    }

    [Fact]
    public void Explicit_earlier_stage_reference_is_preserved()
    {
        List<StageSpec> stages = ParseStages(JsonSingleClipStages(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors", "PreviousStage"),
            MakeStage("UnitTest_Video.safetensors", "Stage0")));

        Assert.Equal(3, stages.Count);
        Assert.Equal("Stage0", stages[2].ImageReference);
    }

    [Fact]
    public void Base2Edit_stage_reference_is_preserved_when_syntax_is_valid()
    {
        List<StageSpec> stages = ParseStages(JsonSingleClipStages(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors", "edit0")));

        Assert.Equal(2, stages.Count);
        Assert.Equal("edit0", stages[1].ImageReference);
    }

    [Fact]
    public void Parser_assigns_linear_stage_ids_from_json_order()
    {
        List<StageSpec> stages = ParseStages(JsonSingleClipStages(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors")));

        Assert.Equal([0, 1, 2], stages.Select(stage => stage.Id).ToArray());
    }

    [Fact]
    public void Missing_later_stage_image_reference_defaults_to_previous_stage()
    {
        JObject firstStage = MakeStage("UnitTest_Video.safetensors");
        firstStage.Remove("imageReference");

        JObject secondStage = MakeStage("UnitTest_Video.safetensors");
        secondStage.Remove("imageReference");

        List<StageSpec> stages = ParseStages(JsonSingleClipStages(firstStage, secondStage));

        Assert.Equal(2, stages.Count);
        Assert.Equal("Generated", stages[0].ImageReference);
        Assert.Equal("PreviousStage", stages[1].ImageReference);
    }

    [Fact]
    public void Text_to_video_workflows_force_generated_image_reference()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        T2IParamInput input = BuildInput(JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Base"),
            MakeStage(models.VideoModel.Name, "Refiner")));
        input.Set(T2IParamTypes.Model, models.VideoModel);
        input.Set(T2IParamTypes.Text2VideoFrames, 25);

        List<StageSpec> stages = ParseStages(input);

        Assert.Equal(2, stages.Count);
        Assert.All(stages, stage => Assert.Equal("Generated", stage.ImageReference));
    }

    [Fact]
    public void Invalid_json_throws_user_error()
    {
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(() =>
            ParseStages("{ definitely-not-json"));
        Assert.Contains("Could not parse Video Stages JSON", ex.Message);
    }

    [Fact]
    public void First_stage_json_upscale_fields_are_normalized_to_defaults_when_non_trivial()
    {
        JObject stage = MakeStage("UnitTest_Video.safetensors");
        stage["upscale"] = 2.5;
        stage["upscaleMethod"] = "model-unit-test-upscaler";

        List<StageSpec> stages = ParseStages(JsonSingleClipStages(stage));

        Assert.Single(stages);
        Assert.Equal(1.0, stages[0].Upscale);
        Assert.Equal("pixel-lanczos", stages[0].UpscaleMethod);
    }

    [Fact]
    public void First_stage_json_control_is_enforced_to_one_when_non_trivial()
    {
        JObject stage = MakeStage("UnitTest_Video.safetensors");
        stage["control"] = 0.35;

        List<StageSpec> stages = ParseStages(JsonSingleClipStages(stage));

        Assert.Single(stages);
        Assert.Equal(1.0, stages[0].Control);
    }

    [Fact]
    public void ParseStages_truncates_nearly_unity_upscale_to_exactly_one_point_zero()
    {
        JObject first = MakeStage("UnitTest_Video.safetensors");
        JObject second = MakeStage("UnitTest_Video.safetensors", "PreviousStage");
        second["upscale"] = "1.0000000001";

        List<StageSpec> stages = ParseStages(JsonSingleClipStages(first, second));

        Assert.Equal(2, stages.Count);
        Assert.Equal(1.0, stages[0].Upscale);
        Assert.Equal(1.0, stages[1].Upscale);
    }

    [Fact]
    public void ParseStages_truncates_stage_numeric_fields_to_documented_precision()
    {
        JObject first = MakeStage("UnitTest_Video.safetensors");
        JObject second = MakeStage("UnitTest_Video.safetensors", "PreviousStage");
        second["control"] = 0.129;
        second["upscale"] = 1.239;
        second["cfgScale"] = 6.29;

        List<StageSpec> stages = ParseStages(JsonSingleClipStages(first, second));

        Assert.Equal(2, stages.Count);
        Assert.Equal(0.12, stages[1].Control);
        Assert.Equal(1.25, stages[1].Upscale);
        Assert.Equal(6.2, stages[1].CfgScale);
    }

    [Fact]
    public void Second_stage_json_preserves_upscale_fields()
    {
        JObject first = MakeStage("UnitTest_Video.safetensors");
        first["upscale"] = 2.5;
        first["upscaleMethod"] = "model-unit-test-upscaler";
        JObject second = MakeStage("UnitTest_Video.safetensors");
        second["upscale"] = 2.0;
        second["upscaleMethod"] = "pixel-bicubic";

        List<StageSpec> stages = ParseStages(JsonSingleClipStages(first, second));

        Assert.Equal(2, stages.Count);
        Assert.Equal(1.0, stages[0].Upscale);
        Assert.Equal("pixel-lanczos", stages[0].UpscaleMethod);
        Assert.Equal(2.0, stages[1].Upscale);
        Assert.Equal("pixel-bicubic", stages[1].UpscaleMethod);
    }

    [Fact]
    public void Missing_stage_defaults_do_not_inherit_base_or_refiner_values()
    {
        using SwarmUiTestContext testContext = new();
        _ = WorkflowTestHarness.VideoStagesSteps();

        T2IParamInput input = BuildInput(JsonSingleClipStages(
            new JObject
            {
                ["model"] = "UnitTest_Video.safetensors"
            }));
        input.Set(T2IParamTypes.RefinerUpscale, 1.5);
        input.Set(ComfyUIBackendExtension.RefinerUpscaleMethod, "model-unit-test-upscaler");
        input.Set(T2IParamTypes.CFGScale, 12.5);
        input.Set(T2IParamTypes.VideoCFG, 6.25);

        List<StageSpec> stages = ParseStages(input);

        Assert.Single(stages);
        Assert.Equal(1.0, stages[0].Control);
        Assert.Equal(1.0, stages[0].Upscale);
        Assert.Equal("pixel-lanczos", stages[0].UpscaleMethod);
        Assert.Equal(6.2, stages[0].CfgScale);
        Assert.Equal("euler", stages[0].Sampler);
        Assert.Equal("normal", stages[0].Scheduler);
    }
}
