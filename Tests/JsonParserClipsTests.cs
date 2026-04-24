using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class JsonParserClipsTests
{
    private static JObject MakeStage(string model, double cfg = 1, int steps = 8)
    {
        return new JObject
        {
            ["Model"] = model,
            ["Steps"] = steps,
            ["CfgScale"] = cfg,
            ["Sampler"] = "euler",
            ["Scheduler"] = "normal",
            ["Vae"] = "",
            ["Control"] = 1,
            ["Upscale"] = 1,
            ["UpscaleMethod"] = "pixel-lanczos",
        };
    }

    private static JObject MakeRef(string source, int frame = 1, bool fromEnd = false)
    {
        return new JObject
        {
            ["Source"] = source,
            ["Frame"] = frame,
            ["FromEnd"] = fromEnd,
        };
    }

    private static JObject MakeClip(
        IEnumerable<JObject> stages,
        IEnumerable<JObject> refs = null,
        bool skipped = false,
        double duration = 3.0,
        string audioSource = VideoStagesExtension.AudioSourceNative,
        bool saveAudioTrack = false,
        int width = 1024,
        int height = 768,
        JObject uploadedAudio = null)
    {
        JObject clip = new()
        {
            ["Skipped"] = skipped,
            ["Duration"] = duration,
            ["AudioSource"] = audioSource,
            ["SaveAudioTrack"] = saveAudioTrack,
            ["Width"] = width,
            ["Height"] = height,
            ["Refs"] = new JArray(refs ?? []),
            ["Stages"] = new JArray(stages),
        };
        if (uploadedAudio is not null)
        {
            clip["UploadedAudio"] = uploadedAudio;
        }
        return clip;
    }

    private static JObject MakeRootConfig(
        int width,
        int height,
        IEnumerable<JObject> clips)
    {
        return new JObject
        {
            ["Width"] = width,
            ["Height"] = height,
            ["Clips"] = new JArray(clips),
        };
    }

    private static JObject MakeUploadedAudio(
        string data = "data:audio/wav;base64,QUJD",
        string fileName = "clip.wav")
    {
        return new JObject
        {
            ["Data"] = data,
            ["FileName"] = fileName,
        };
    }

    private static T2IParamInput BuildInputWithJson(string json)
    {
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        T2IParamInput input = new(null);
        input.Set(VideoStagesExtension.VideoStagesJson, json);
        return input;
    }

    private static JsonParser BuildParser(string json)
    {
        T2IParamInput input = BuildInputWithJson(json);
        WorkflowGenerator generator = new() { UserInput = input };
        return new JsonParser(generator);
    }


    [Fact]
    public void ParseClips_ClipShape_PopulatesPerClipFields()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 12, fromEnd: true)],
                duration: 4.0,
                saveAudioTrack: true,
                width: 800,
                height: 600),
            MakeClip(
                stages: [MakeStage("model-b"), MakeStage("model-c")],
                duration: 6.0)
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.ClipSpec> clips = parser.ParseClips();

        Assert.Equal(2, clips.Count);
        Assert.Equal(0, clips[0].Id);
        Assert.Equal(4.0, clips[0].DurationSeconds);
        Assert.True(clips[0].SaveAudioTrack);
        Assert.Equal(800, clips[0].Width);
        Assert.Equal(600, clips[0].Height);
        Assert.Equal(2, clips[0].Refs.Count);
        Assert.Equal("Base", clips[0].Refs[0].Source);
        Assert.Equal(1, clips[0].Refs[0].Frame);
        Assert.Equal("Refiner", clips[0].Refs[1].Source);
        Assert.Equal(12, clips[0].Refs[1].Frame);
        Assert.True(clips[0].Refs[1].FromEnd);
        Assert.Single(clips[0].Stages);
        Assert.Equal("model-a", clips[0].Stages[0].Model);

        Assert.Equal(1, clips[1].Id);
        Assert.Equal(6.0, clips[1].DurationSeconds);
        Assert.False(clips[1].SaveAudioTrack);
        Assert.Empty(clips[1].Refs);
        Assert.Equal(2, clips[1].Stages.Count);
    }

    [Fact]
    public void ParseConfig_RootShape_PopulatesRootDimensionsAndClipAudioSource()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1344,
            height: 832,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    audioSource: VideoStagesExtension.AudioSourceUpload)
            ]));
        JsonParser parser = BuildParser(json);

        JsonParser.VideoStagesSpec config = parser.ParseConfig();

        Assert.Equal(1344, config.Width);
        Assert.Equal(832, config.Height);
        Assert.Single(config.Clips);
        Assert.Equal(VideoStagesExtension.AudioSourceUpload, config.Clips[0].AudioSource);
    }

    [Fact]
    public void ParseClips_PerClipUploadedAudio_IsParsed()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                audioSource: VideoStagesExtension.AudioSourceUpload,
                uploadedAudio: MakeUploadedAudio(fileName: "first.wav")),
            MakeClip(
                stages: [MakeStage("model-b")],
                audioSource: VideoStagesExtension.AudioSourceUpload,
                uploadedAudio: MakeUploadedAudio(fileName: "second.wav"))
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.ClipSpec> clips = parser.ParseClips();

        Assert.Equal(2, clips.Count);
        Assert.NotNull(clips[0].UploadedAudio);
        Assert.Equal("first.wav", clips[0].UploadedAudio.FileName);
        Assert.NotNull(clips[1].UploadedAudio);
        Assert.Equal("second.wav", clips[1].UploadedAudio.FileName);

        AudioFile firstAudio = parser.ParseUploadedAudioForClip(clips[0]);
        AudioFile secondAudio = parser.ParseUploadedAudioForClip(clips[1]);
        Assert.NotNull(firstAudio);
        Assert.Equal("first.wav", firstAudio.SourceFilePath);
        Assert.NotNull(secondAudio);
        Assert.Equal("second.wav", secondAudio.SourceFilePath);
    }

    [Fact]
    public void ParseUploadedAudioForClip_InputPath_WithoutSession_ReturnsNull()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                audioSource: VideoStagesExtension.AudioSourceUpload,
                uploadedAudio: new JObject
                {
                    ["Data"] = "inputs/_comfy1/clip_part02.wav",
                    ["FileName"] = "clip_part02.wav",
                })));
        JsonParser parser = BuildParser(json);

        JsonParser.ClipSpec clip = parser.ParseClips().Single();

        Assert.NotNull(clip.UploadedAudio);
        Assert.Equal("inputs/_comfy1/clip_part02.wav", clip.UploadedAudio.Data);

        AudioFile audio = parser.ParseUploadedAudioForClip(clip);

        Assert.Null(audio);
    }

    [Fact]
    public void ParseStages_Flattens_ClipShape_AcrossClips_AssigningSequentialIds()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")]),
            MakeClip(stages: [MakeStage("model-c")])
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Equal(3, stages.Count);
        Assert.Equal(0, stages[0].Id);
        Assert.Equal(1, stages[1].Id);
        Assert.Equal(2, stages[2].Id);
        Assert.Equal("model-a", stages[0].Model);
        Assert.Equal("model-b", stages[1].Model);
        Assert.Equal("model-c", stages[2].Model);
    }

    [Fact]
    public void ParseStages_SkipsSkippedClipsAndStages()
    {
        JObject skippedStage = MakeStage("model-skip");
        skippedStage["Skipped"] = true;

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip( stages: [MakeStage("model-a"), skippedStage]),
            MakeClip( stages: [MakeStage("model-skipped-clip")], skipped: true),
            MakeClip( stages: [MakeStage("model-c")])
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Equal(2, stages.Count);
        Assert.Equal("model-a", stages[0].Model);
        Assert.Equal("model-c", stages[1].Model);
    }

    [Fact]
    public void ParseStages_RootShape_UsesRootDimensionsAcrossClips()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0,
                    width: 800,
                    height: 600)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        JsonParser parser = new(generator);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Single(stages);
        Assert.Equal(0, stages[0].ClipId);
        Assert.Equal(VideoStagesExtension.AudioSourceNative, stages[0].ClipAudioSource);
        Assert.Equal(1280, stages[0].ClipWidth);
        Assert.Equal(720, stages[0].ClipHeight);
        Assert.Equal(97, stages[0].ClipFrames);
    }

    [Fact]
    public void ParseStages_RegisteredRootParams_OverrideJsonRootDimensions()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0,
                    width: 800,
                    height: 600)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(VideoStagesExtension.RootWidth, 1536);
        input.Set(VideoStagesExtension.RootHeight, 864);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        JsonParser parser = new(generator);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Single(stages);
        Assert.Equal(1536, stages[0].ClipWidth);
        Assert.Equal(864, stages[0].ClipHeight);
        Assert.Equal(97, stages[0].ClipFrames);
    }

    [Fact]
    public void ParseStages_RegisteredRootFps_OverridesCoreVideoFpsForClipDurationFrames()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0,
                    width: 800,
                    height: 600)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(VideoStagesExtension.RootFPS, 32);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        JsonParser parser = new(generator);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Single(stages);
        Assert.Equal(129, stages[0].ClipFrames);
        Assert.Equal(32, stages[0].ClipFPS);
    }

    [Fact]
    public void ParseClips_StagesMissingModel_AreSkippedSilently()
    {
        JObject brokenStage = MakeStage("");
        brokenStage["Model"] = "";

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip( stages: [brokenStage, MakeStage("model-a")])
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.ClipSpec> clips = parser.ParseClips();

        Assert.Single(clips);
        Assert.Single(clips[0].Stages);
        Assert.Equal("model-a", clips[0].Stages[0].Model);
    }

    [Fact]
    public void ParseClips_EmptyJson_ReturnsEmpty()
    {
        JsonParser parser = BuildParser("[]");
        Assert.Empty(parser.ParseClips());
        Assert.Empty(parser.ParseStages());
    }

    [Fact]
    public void ParseClips_InvalidJson_ReturnsEmpty()
    {
        JsonParser parser = BuildParser("not json at all");
        Assert.Empty(parser.ParseClips());
        Assert.Empty(parser.ParseStages());
    }

    [Fact]
    public void ParseClips_RefWithMissingSource_IsSkipped()
    {
        JObject brokenRef = new() { ["Frame"] = 4 };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                refs: [brokenRef, MakeRef("Base")])
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.ClipSpec> clips = parser.ParseClips();

        Assert.Single(clips);
        Assert.Single(clips[0].Refs);
        Assert.Equal("Base", clips[0].Refs[0].Source);
    }

    [Fact]
    public void ParseStages_PropagatesClipDimensionsAndFramesIntoEachStage()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                duration: 4.0,
                width: 800,
                height: 600),
            MakeClip(
                stages: [MakeStage("model-b")],
                duration: 2.0,
                width: 1920,
                height: 1080)
        ));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        JsonParser parser = new(generator);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Equal(2, stages.Count);
        Assert.Equal(800, stages[0].ClipWidth);
        Assert.Equal(600, stages[0].ClipHeight);
        Assert.Equal(97, stages[0].ClipFrames);
        Assert.Equal(1920, stages[1].ClipWidth);
        Assert.Equal(1080, stages[1].ClipHeight);
        Assert.Equal(49, stages[1].ClipFrames);
    }

    [Theory]
    [InlineData(10.0, 241)]
    [InlineData(21.5, 521)]
    public void ParseStages_ClipDurationFrames_AreAlignedUpToEightPlusOne(double duration, int expectedFrames)
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: duration,
                    width: 800,
                    height: 600)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        JsonParser parser = new(generator);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Single(stages);
        Assert.Equal(expectedFrames, stages[0].ClipFrames);
    }

    [Fact]
    public void ParseClips_PreservesUploadFileName()
    {
        JObject uploadRef = new()
        {
            ["Source"] = "Upload",
            ["UploadFileName"] = "ref.png",
        };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                refs: [uploadRef])
        ));
        JsonParser parser = BuildParser(json);

        JsonParser.ClipSpec clip = parser.ParseClips().Single();
        Assert.Equal("Upload", clip.Refs[0].Source);
        Assert.Equal("ref.png", clip.Refs[0].UploadFileName);
    }

    [Fact]
    public void ParseClips_RefUpload_ReadsNestedUploadedImagePayload()
    {
        const string imageData = "data:image/png;base64,QUJDREVG";
        JObject uploadRef = new()
        {
            ["Source"] = "Upload",
            ["UploadedImage"] = new JObject
            {
                ["Data"] = imageData,
                ["FileName"] = "guide.png",
            },
        };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                refs: [uploadRef])
        ));
        JsonParser parser = BuildParser(json);

        JsonParser.ClipSpec clip = parser.ParseClips().Single();
        JsonParser.RefSpec r = clip.Refs[0];
        Assert.Equal("Upload", r.Source);
        Assert.Equal(imageData, r.Data);
        Assert.Equal("guide.png", r.UploadFileName);
    }

    [Fact]
    public void ParseClips_RefUpload_NestedUploadedImage_OverridesTopLevelData()
    {
        JObject uploadRef = new()
        {
            ["Source"] = "Upload",
            ["Data"] = "data:image/png;base64,T1BQ",
            ["UploadedImage"] = new JObject
            {
                ["Data"] = "data:image/png;base64,TkVTVA==",
                ["FileName"] = "nested.png",
            },
        };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                refs: [uploadRef])
        ));
        JsonParser parser = BuildParser(json);

        JsonParser.RefSpec r = parser.ParseClips().Single().Refs[0];
        Assert.Equal("data:image/png;base64,TkVTVA==", r.Data);
        Assert.Equal("nested.png", r.UploadFileName);
    }

    [Fact]
    public void ParseStages_FlattenedStagesIncludeClipRefsAndNormalizedRefStrengths()
    {
        JObject stage = MakeStage("model-a");
        stage["refStrengths"] = new JArray(0.55, 0.66);
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [stage],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 9)])));

        JsonParser parser = BuildParser(json);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        JsonParser.StageSpec flattened = Assert.Single(stages);
        Assert.Equal(2, flattened.ClipRefs.Count);
        Assert.Equal(2, flattened.RefStrengths.Count);
        Assert.Equal(0.55, flattened.RefStrengths[0]);
        Assert.Equal(0.66, flattened.RefStrengths[1]);
    }

    [Fact]
    public void ParseStages_PadsMissingRefStrengthsToMatchReferenceCount()
    {
        JObject stage = MakeStage("model-a");
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [stage],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 2)])));

        JsonParser parser = BuildParser(json);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        JsonParser.StageSpec flattened = Assert.Single(stages);
        Assert.Equal(2, flattened.RefStrengths.Count);
        Assert.All(
            flattened.RefStrengths,
            strength => Assert.Equal(VideoStagesExtension.DefaultStageRefStrength, strength));
    }
}
