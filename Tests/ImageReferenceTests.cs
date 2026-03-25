using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class ImageReferenceTests
{
    private static JObject MakeStage(string model, string imageReference = null) =>
        new()
        {
            ["Control"] = 1.0,
            ["Upscale"] = 1.0,
            ["UpscaleMethod"] = "pixel-lanczos",
            ["Model"] = model,
            ["Vae"] = "",
            ["Steps"] = 12,
            ["CfgScale"] = 5.0,
            ["Sampler"] = "euler",
            ["Scheduler"] = "normal",
            ["ImageReference"] = imageReference
        };

    private static T2IParamInput BuildInput(string stagesJson)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, "unit test prompt");
        input.Set(VideoStagesExtension.VideoStagesJson, stagesJson);
        return input;
    }

    private static List<JsonParser.StageSpec> ParseStages(T2IParamInput input)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/"
        };
        return new JsonParser(generator).ParseStages();
    }

    private static List<JsonParser.StageSpec> ParseStages(string stagesJson)
    {
        return ParseStages(BuildInput(stagesJson));
    }

    [Fact]
    public void Stage_zero_defaults_to_generated_and_future_reference_falls_back_to_previous_stage()
    {
        List<JsonParser.StageSpec> stages = ParseStages(new JArray(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors", "Stage9")
        ).ToString());

        Assert.Equal(2, stages.Count);
        Assert.Equal("Generated", stages[0].ImageReference);
        Assert.Equal("PreviousStage", stages[1].ImageReference);
    }

    [Fact]
    public void Explicit_earlier_stage_reference_is_preserved()
    {
        List<JsonParser.StageSpec> stages = ParseStages(new JArray(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors", "PreviousStage"),
            MakeStage("UnitTest_Video.safetensors", "Stage0")
        ).ToString());

        Assert.Equal(3, stages.Count);
        Assert.Equal("Stage0", stages[2].ImageReference);
    }

    [Fact]
    public void Parser_assigns_linear_stage_ids_from_json_order()
    {
        List<JsonParser.StageSpec> stages = ParseStages(new JArray(
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors"),
            MakeStage("UnitTest_Video.safetensors")
        ).ToString());

        Assert.Equal([0, 1, 2], stages.Select(stage => stage.Id).ToArray());
    }

    [Fact]
    public void Missing_later_stage_image_reference_defaults_to_previous_stage()
    {
        JObject firstStage = MakeStage("UnitTest_Video.safetensors");
        firstStage.Remove("ImageReference");

        JObject secondStage = MakeStage("UnitTest_Video.safetensors");
        secondStage.Remove("ImageReference");

        List<JsonParser.StageSpec> stages = ParseStages(new JArray(firstStage, secondStage).ToString());

        Assert.Equal(2, stages.Count);
        Assert.Equal("Generated", stages[0].ImageReference);
        Assert.Equal("PreviousStage", stages[1].ImageReference);
    }

    [Fact]
    public void Invalid_json_is_ignored_safely()
    {
        List<JsonParser.StageSpec> stages = ParseStages("{ definitely-not-json");
        Assert.Empty(stages);
    }

    [Fact]
    public void Missing_stage_defaults_do_not_inherit_base_or_refiner_values()
    {
        using SwarmUiTestContext testContext = new();
        _ = WorkflowTestHarness.VideoStagesSteps();
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        UnitTestStubs.EnsureComfyVideoParamsRegistered();

        T2IParamInput input = BuildInput(new JArray(
            new JObject
            {
                ["Model"] = "UnitTest_Video.safetensors"
            }
        ).ToString());
        input.Set(T2IParamTypes.RefinerUpscale, 1.5);
        input.Set(ComfyUIBackendExtension.RefinerUpscaleMethod, "model-unit-test-upscaler");
        input.Set(T2IParamTypes.CFGScale, 12.5);
        input.Set(T2IParamTypes.VideoCFG, 6.25);

        List<JsonParser.StageSpec> stages = ParseStages(input);

        Assert.Single(stages);
        Assert.Equal(1.0, stages[0].Control);
        Assert.Equal(1.0, stages[0].Upscale);
        Assert.Equal("pixel-lanczos", stages[0].UpscaleMethod);
        Assert.Equal("", stages[0].Vae);
        Assert.Equal(6.25, stages[0].CfgScale);
        Assert.Equal("euler", stages[0].Sampler);
        Assert.Equal("normal", stages[0].Scheduler);
    }
}
