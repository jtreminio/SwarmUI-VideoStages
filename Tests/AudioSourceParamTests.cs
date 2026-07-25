using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioSourceParamTests
{
    private static JObject MakeStage() => new()
    {
        ["model"] = "unit-model",
        ["steps"] = 8,
        ["cfgScale"] = 1,
        ["sampler"] = "euler",
        ["scheduler"] = "normal",
        ["control"] = 1,
        ["upscale"] = 1,
        ["upscaleMethod"] = "pixel-lanczos",
    };

    private static string BuildConfigJson(string audioSource) => JsonConvert.SerializeObject(new JObject
    {
        ["width"] = 1024,
        ["height"] = 576,
        ["clips"] = new JArray(
            new JObject
            {
                ["audioSource"] = audioSource,
                ["stages"] = new JArray(MakeStage())
            })
    });

    [Theory]
    [InlineData("audio0")]
    [InlineData("audio1")]
    public void Clip_audio_source_preserves_runtime_augmented_values(string value)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, BuildConfigJson(value));
        WorkflowGenerator generator = new() { UserInput = input };

        ClipSpec clip = Assert.Single(VideoStagesSpecParser.Parse(generator).Clips);

        Assert.Equal(value, clip.AudioSource);
    }
}
