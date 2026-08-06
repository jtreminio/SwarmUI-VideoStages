using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using VideoStages.Authoring;

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
            ["control"] = control,
            ["upscale"] = upscale,
            ["upscaleMethod"] = upscaleMethod,
            ["model"] = model,
            ["steps"] = steps,
            ["cfgScale"] = cfgScale,
            ["sampler"] = sampler,
            ["scheduler"] = scheduler,
            ["imageReference"] = imageReference
        };

    public static JObject MakeClip(params JObject[] stages) =>
        new()
        {
            ["stages"] = new JArray(stages)
        };

    /// <summary>A clip of an authored length in seconds.</summary>
    public static JObject MakeClip(double duration, params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        clip["duration"] = duration;
        return clip;
    }

    /// <summary>Uploaded footage a clip refines instead of generating from noise.</summary>
    public static JObject SourceVideo() => new()
    {
        ["data"] = "data:video/mp4;base64," + Convert.ToBase64String([0xDE, 0xAD, 0xBE, 0xEF]),
        ["fileName"] = "refine.mp4",
        ["startSeconds"] = 0.0,
    };

    /// <summary>
    /// A clip that refines uploaded footage instead of generating from noise, opening
    /// <paramref name="startSeconds"/> into that footage. Stage 0's default <c>Generated</c>
    /// reference is stripped: it would pull in a root donor alongside the source.
    /// </summary>
    public static JObject SourceClip(double duration, double startSeconds, params JObject[] stages)
    {
        if (stages.Length > 0)
        {
            stages[0].Remove("imageReference");
        }
        JObject clip = MakeClip(duration, stages);
        JObject source = SourceVideo();
        source["startSeconds"] = startSeconds;
        clip["initVideo"] = source;
        return clip;
    }

    /// <summary>A retake is only honoured on a sourced clip with a usable duration; 4.0s is the
    /// length <see cref="Ltx2WorkflowFixture.RetakeClipFrames"/> is derived from.</summary>
    public static JObject RetakeClip(JObject retake, params JObject[] stages)
    {
        // Strip MakeStage's "Generated" default so the retake graph has no root donor — keeping it
        // would pull in core's base sampler and a second guide chain.
        stages[0].Remove("imageReference");
        JObject clip = MakeClip(4.0, stages);
        clip["initVideo"] = SourceVideo();
        if (retake is not null)
        {
            clip["retake"] = retake;
        }
        return clip;
    }

    public static JObject MakeClipWithRefs(IEnumerable<JObject> refs = null, params JObject[] stages) =>
        new()
        {
            ["frameRefs"] = new JArray(refs ?? []),
            ["stages"] = new JArray(stages)
        };

    public static JObject MakeRef(string source, int frame = 1, bool fromEnd = false) =>
        new()
        {
            ["source"] = source,
            ["frame"] = frame,
            ["fromEnd"] = fromEnd
        };

    /// <summary>A clip reference carrying its own uploaded image.</summary>
    public static JObject UploadedReference(
        string payload = "AAAA",
        bool fromEnd = false,
        int frame = 1,
        string fileName = null) =>
        new()
        {
            ["source"] = "Upload",
            ["frame"] = frame,
            ["fromEnd"] = fromEnd,
            ["uploadedImage"] = new JObject
            {
                ["data"] = $"data:image/png;base64,{payload}",
                ["fileName"] = fileName ?? (fromEnd ? "last.png" : "first.png"),
            },
        };

    public static JObject UploadedAudio(string fileName = "clip.wav", string payload = "QUJD") =>
        new()
        {
            ["data"] = $"data:audio/wav;base64,{payload}",
            ["fileName"] = fileName,
        };

    /// <summary>A root timeline audio lane; planning projects it onto every clip it overlaps.</summary>
    public static JObject AudioTrack(
        string id,
        double volume,
        string fileName,
        params JObject[] spans) =>
        new()
        {
            ["id"] = id,
            ["volume"] = volume,
            ["source"] = new JObject
            {
                ["kind"] = "Upload",
                ["reference"] = fileName,
                ["uploadedAudio"] = UploadedAudio(fileName),
            },
            ["spans"] = new JArray(spans.Cast<object>().ToArray()),
        };

    public static JObject AudioSpan(
        double timelineStartSeconds,
        double timelineLengthSeconds,
        double sourceStartSeconds) =>
        new()
        {
            ["timelineStartSeconds"] = timelineStartSeconds,
            ["timelineLengthSeconds"] = timelineLengthSeconds,
            ["sourceStartSeconds"] = sourceStartSeconds,
        };

    public static JObject MakeRootConfig(int width, int height, params JObject[] clips) =>
        new()
        {
            ["schemaVersion"] = DocumentJson.SupportedSchemaVersion,
            ["width"] = width,
            ["height"] = height,
            ["clips"] = new JArray(clips)
        };

    public static JObject MakeRootConfig(int width, int height, IEnumerable<JObject> clips) =>
        MakeRootConfig(width, height, clips.ToArray());

    public static JObject MakeRootConfig(params JObject[] clips) =>
        MakeRootConfig(512, 512, clips);

    public static string JsonSingleClipStages(params JObject[] stages) =>
        MakeDocument(MakeClip(stages)).ToString();

    /// <summary>The authoring document envelope the frontend codec emits around a clip list.</summary>
    public static JObject MakeDocument(params JObject[] clips) =>
        new()
        {
            ["schemaVersion"] = DocumentJson.SupportedSchemaVersion,
            ["clips"] = new JArray(clips)
        };

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
        SetVideoStagesConfig(input, stagesJson);
        return input;
    }

    public static void SetVideoStagesConfig(T2IParamInput input, string json)
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        // Config JSON now rides the hidden "Video Stages Data" param, not an inline
        // <videostages>{json} prompt carrier (that carrier was removed).
        input.Set(VideoStagesExtension.Data, StampDocumentEnvelope(json));
        input.Set(VideoStagesExtension.Enabled, true);
    }

    /// <summary>Tests author the document body (a clip array or a root object); this stamps the
    /// current schema version so every test exercises the exact envelope the frontend emits.</summary>
    private static string StampDocumentEnvelope(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }
        JToken token;
        try
        {
            token = JToken.Parse(json);
        }
        catch (JsonReaderException)
        {
            return json;
        }
        JObject document = token switch
        {
            JArray clips => new JObject { ["clips"] = clips },
            JObject obj => obj,
            _ => null,
        };
        if (document is null)
        {
            return json;
        }
        document["schemaVersion"] ??= DocumentJson.SupportedSchemaVersion;
        return document.ToString();
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
        SetVideoStagesConfig(input, stagesJson);
        input.Set(T2IParamTypes.Text2VideoFrames, 25);
        if (Program.T2IModelSets.TryGetValue("Clip", out T2IModelHandler clipHandler)
            && clipHandler.Models.TryGetValue("gemma_3_12B_it.safetensors", out T2IModel gemmaModel))
        {
            input.Set(T2IParamTypes.GemmaModel, gemmaModel);
        }
        return input;
    }
}
