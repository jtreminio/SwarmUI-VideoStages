using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioSourceParamTests
{
    private static JObject MakeStage() => new()
    {
        ["Model"] = "unit-model",
        ["Steps"] = 8,
        ["CfgScale"] = 1,
        ["Sampler"] = "euler",
        ["Scheduler"] = "normal",
        ["Vae"] = "",
        ["Control"] = 1,
        ["Upscale"] = 1,
        ["UpscaleMethod"] = "pixel-lanczos",
    };

    private static string BuildConfigJson(string audioSource) => JsonConvert.SerializeObject(new JObject
    {
        ["Width"] = 1024,
        ["Height"] = 576,
        ["Clips"] = new JArray(
            new JObject
            {
                ["Name"] = "Clip 0",
                ["AudioSource"] = audioSource,
                ["Stages"] = new JArray(MakeStage())
            })
    });

    [Theory]
    [InlineData(VideoStagesExtension.AudioSourceSwarm)]
    [InlineData("acestepfun0")]
    public void Clip_audio_source_preserves_runtime_augmented_values(string value)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(VideoStagesExtension.VideoStagesJson, BuildConfigJson(value));
        WorkflowGenerator generator = new() { UserInput = input };
        JsonParser parser = new(generator);

        JsonParser.ClipSpec clip = Assert.Single(parser.ParseClips());

        Assert.Equal(value, clip.AudioSource);
    }
}
