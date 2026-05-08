using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace VideoStages.Tests;

internal static class Fixtures
{
    public const string LtxV23SpatialUpscaler = "latentmodel-ltx-2.3-spatial-upscaler-x2-1.1.safetensors";

    public static JObject MakeStage(
        string model,
        string imageReference = "Generated",
        double control = 1.0,
        double upscale = 1.0,
        string upscaleMethod = "pixel-lanczos",
        int steps = 12,
        double cfgScale = 4.5,
        string sampler = "euler",
        string scheduler = "normal") =>
        new()
        {
            ["Control"] = control,
            ["Upscale"] = upscale,
            ["UpscaleMethod"] = upscaleMethod,
            ["Model"] = model,
            ["Vae"] = "",
            ["Steps"] = steps,
            ["CfgScale"] = cfgScale,
            ["Sampler"] = sampler,
            ["Scheduler"] = scheduler,
            ["ImageReference"] = imageReference
        };

    public static JObject MakeClip(params JObject[] stages) =>
        new()
        {
            ["Name"] = "Clip 0",
            ["Stages"] = new JArray(stages)
        };

    public static JObject MakeClipWithRefs(IEnumerable<JObject> refs = null, params JObject[] stages) =>
        new()
        {
            ["Name"] = "Clip 0",
            ["Refs"] = new JArray(refs ?? []),
            ["Stages"] = new JArray(stages)
        };

    public static JObject MakeRef(string source, int frame = 1, bool fromEnd = false) =>
        new()
        {
            ["Source"] = source,
            ["Frame"] = frame,
            ["FromEnd"] = fromEnd
        };

    public static JObject MakeRootConfig(int width, int height, params JObject[] clips) =>
        new()
        {
            ["Width"] = width,
            ["Height"] = height,
            ["Clips"] = new JArray(clips)
        };

    public static JObject MakeRootConfig(int width, int height, IEnumerable<JObject> clips) =>
        MakeRootConfig(width, height, clips.ToArray());

    public static JObject MakeRootConfig(params JObject[] clips) =>
        MakeRootConfig(512, 512, clips);

    public static string JsonSingleClipStages(params JObject[] stages) =>
        new JArray(MakeClip(stages)).ToString();

    public static T2IParamInput BuildInput(T2IModel baseModel, string stagesJson, string prompt = "unit test prompt")
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(VideoStagesExtension.VideoStagesJson, stagesJson);
        return input;
    }

    public static T2IParamInput BuildNativeInput(
        T2IModel baseModel,
        T2IModel videoModel,
        string stagesJson,
        string prompt = "unit test prompt")
    {
        T2IParamInput input = BuildInput(baseModel, stagesJson, prompt: prompt);
        input.Set(T2IParamTypes.VideoModel, videoModel);
        input.Set(T2IParamTypes.VideoFrames, 16);
        input.Set(T2IParamTypes.VideoFPS, 24);
        if (Program.T2IModelSets.TryGetValue("Clip", out T2IModelHandler clipHandler)
            && clipHandler.Models.TryGetValue("gemma_3_12B_it.safetensors", out T2IModel gemmaModel))
        {
            input.Set(T2IParamTypes.GemmaModel, gemmaModel);
        }
        return input;
    }

    public static T2IParamInput BuildTextToVideoInput(
        T2IModel videoModel,
        string stagesJson,
        string prompt = "unit test prompt")
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, videoModel);
        input.Set(VideoStagesExtension.VideoStagesJson, stagesJson);
        input.Set(T2IParamTypes.Text2VideoFrames, 25);
        if (Program.T2IModelSets.TryGetValue("Clip", out T2IModelHandler clipHandler)
            && clipHandler.Models.TryGetValue("gemma_3_12B_it.safetensors", out T2IModel gemmaModel))
        {
            input.Set(T2IParamTypes.GemmaModel, gemmaModel);
        }
        return input;
    }
}
