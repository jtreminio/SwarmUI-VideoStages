using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class VideoStagesSpecParserClipsTests
{
    // Local override of Fixtures.MakeStage: parser tests omit ImageReference entirely (asserts absence behavior)
    // and use lighter defaults (cfg=1, steps=8) since these tests don't exercise sampling.
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

    private static JObject MakeClip(
        IEnumerable<JObject> stages,
        IEnumerable<JObject> refs = null,
        bool skipped = false,
        double duration = 3.0,
        string audioSource = Constants.AudioSourceNative,
        string controlNetSource = Constants.ControlNetSourceOne,
        bool saveAudioTrack = false,
        bool clipLengthFromAudio = false,
        bool clipLengthFromControlNet = false,
        bool reuseAudio = false,
        JObject uploadedAudio = null)
    {
        JObject clip = new()
        {
            ["Skipped"] = skipped,
            ["Duration"] = duration,
            ["AudioSource"] = audioSource,
            ["ControlNetSource"] = controlNetSource,
            ["SaveAudioTrack"] = saveAudioTrack,
            ["ClipLengthFromAudio"] = clipLengthFromAudio,
            ["ClipLengthFromControlNet"] = clipLengthFromControlNet,
            ["ReuseAudio"] = reuseAudio,
            ["Refs"] = new JArray(refs ?? []),
            ["Stages"] = new JArray(stages),
        };
        if (uploadedAudio is not null)
        {
            clip["UploadedAudio"] = uploadedAudio;
        }
        return clip;
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

    private static WorkflowGenerator BuildParser(string json)
    {
        T2IParamInput input = BuildInputWithJson(json);
        return new() { UserInput = input };
    }

    private static List<StageSpec> FlattenedActiveStages(WorkflowGenerator parser) =>
        [.. VideoStagesSpecParser.Parse(parser).Clips.SelectMany(c => c.Stages)];


    [Fact]
    public void ParseClips_ClipShape_PopulatesPerClipFields()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 12, fromEnd: true)],
                duration: 4.0,
                controlNetSource: Constants.ControlNetSourceTwo,
                saveAudioTrack: true,
                clipLengthFromAudio: true,
                clipLengthFromControlNet: true,
                reuseAudio: true),
            MakeClip(
                stages: [MakeStage("model-b"), MakeStage("model-c")],
                duration: 6.0)
        ));
        WorkflowGenerator parser = BuildParser(json);

        IReadOnlyList<ClipSpec> clips = VideoStagesSpecParser.Parse(parser).Clips;

        Assert.Equal(2, clips.Count);
        Assert.Equal(0, clips[0].Id);
        Assert.Equal(Constants.ControlNetSourceTwo, clips[0].ControlNetSource);
        Assert.True(clips[0].SaveAudioTrack);
        Assert.False(clips[0].ClipLengthFromAudio);
        Assert.True(clips[0].ClipLengthFromControlNet);
        Assert.True(clips[0].ReuseAudio);
        Assert.Equal(2, clips[0].ImageRefs.Count);
        Assert.Equal("Base", clips[0].ImageRefs[0].Source);
        Assert.Equal(1, clips[0].ImageRefs[0].Frame);
        Assert.Equal("Refiner", clips[0].ImageRefs[1].Source);
        Assert.Equal(12, clips[0].ImageRefs[1].Frame);
        Assert.True(clips[0].ImageRefs[1].FromEnd);
        Assert.Single(clips[0].Stages);
        Assert.Equal("model-a", clips[0].Stages[0].Model);

        Assert.Equal(1, clips[1].Id);
        Assert.False(clips[1].SaveAudioTrack);
        Assert.Empty(clips[1].ImageRefs);
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
                    audioSource: Constants.AudioSourceUpload)
            ]));
        WorkflowGenerator parser = BuildParser(json);

        VideoStagesSpec config = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(1344, config.Width);
        Assert.Equal(832, config.Height);
        Assert.Single(config.Clips);
        Assert.Equal(Constants.AudioSourceUpload, config.Clips[0].AudioSource);
    }

    [Fact]
    public void ParseClips_PerClipUploadedAudio_IsParsed()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                audioSource: Constants.AudioSourceUpload,
                uploadedAudio: MakeUploadedAudio(fileName: "first.wav")),
            MakeClip(
                stages: [MakeStage("model-b")],
                audioSource: Constants.AudioSourceUpload,
                uploadedAudio: MakeUploadedAudio(fileName: "second.wav"))
        ));
        WorkflowGenerator parser = BuildParser(json);

        IReadOnlyList<ClipSpec> clips = VideoStagesSpecParser.Parse(parser).Clips;

        Assert.Equal(2, clips.Count);
        Assert.Equal("first.wav", clips[0].UploadedAudio.FileName);
        Assert.Equal("second.wav", clips[1].UploadedAudio.FileName);

        AudioFile firstAudio = VideoStagesSpecParser.MaterializeUploadedAudioForClip(parser, clips[0]);
        AudioFile secondAudio = VideoStagesSpecParser.MaterializeUploadedAudioForClip(parser, clips[1]);
        Assert.Equal("first.wav", firstAudio.SourceFilePath);
        Assert.Equal("second.wav", secondAudio.SourceFilePath);
    }

    [Fact]
    public void ParseUploadedAudioForClip_InputPath_WithoutSession_ReturnsNull()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                audioSource: Constants.AudioSourceUpload,
                uploadedAudio: new JObject
                {
                    ["Data"] = "inputs/_comfy1/clip_part02.wav",
                    ["FileName"] = "clip_part02.wav",
                })));
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec clip = VideoStagesSpecParser.Parse(parser).Clips.Single();

        Assert.Equal("inputs/_comfy1/clip_part02.wav", clip.UploadedAudio.Data);

        AudioFile audio = VideoStagesSpecParser.MaterializeUploadedAudioForClip(parser, clip);

        Assert.Null(audio);
    }

    [Fact]
    public void ParseConfig_Flattens_ClipShape_AcrossClips_AssigningSequentialIds()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")]),
            MakeClip(stages: [MakeStage("model-c")])
        ));
        WorkflowGenerator parser = BuildParser(json);

        List<StageSpec> stages = FlattenedActiveStages(parser);

        Assert.Equal(3, stages.Count);
        Assert.Equal(0, stages[0].Id);
        Assert.Equal(1, stages[1].Id);
        Assert.Equal(2, stages[2].Id);
        Assert.Equal("model-a", stages[0].Model);
        Assert.Equal("model-b", stages[1].Model);
        Assert.Equal("model-c", stages[2].Model);
    }

    [Fact]
    public void ParseConfig_EnforcesStageZeroControlPerClip()
    {
        JObject clipZeroStageZero = MakeStage("model-a");
        clipZeroStageZero["Control"] = 0.25;
        JObject clipZeroStageOne = MakeStage("model-b");
        clipZeroStageOne["Control"] = 0.35;
        JObject clipOneStageZero = MakeStage("model-c");
        clipOneStageZero["Control"] = 0.45;

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [clipZeroStageZero, clipZeroStageOne]),
            MakeClip(stages: [clipOneStageZero])
        ));
        WorkflowGenerator parser = BuildParser(json);

        List<StageSpec> stages = FlattenedActiveStages(parser);

        Assert.Equal(3, stages.Count);
        Assert.Equal(1.0, stages[0].Control);
        Assert.Equal(0.35, stages[1].Control);
        Assert.Equal(1.0, stages[2].Control);
    }

    [Fact]
    public void ParseConfig_SkipsSkippedClipsAndStages()
    {
        JObject skippedStage = MakeStage("model-skip");
        skippedStage["Skipped"] = true;

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip( stages: [MakeStage("model-a"), skippedStage]),
            MakeClip( stages: [MakeStage("model-skipped-clip")], skipped: true),
            MakeClip( stages: [MakeStage("model-c")])
        ));
        WorkflowGenerator parser = BuildParser(json);

        List<StageSpec> stages = FlattenedActiveStages(parser);

        Assert.Equal(2, stages.Count);
        Assert.Equal("model-a", stages[0].Model);
        Assert.Equal("model-c", stages[1].Model);
    }

    [Fact]
    public void ParseConfig_RootShape_UsesRootDimensionsAcrossClips()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        ClipSpec clip = Assert.Single(spec.Clips);
        StageSpec stage = Assert.Single(clip.Stages);
        Assert.Equal(0, clip.Id);
        Assert.Equal(Constants.AudioSourceNative, clip.AudioSource);
        Assert.Equal(Constants.ControlNetSourceOne, clip.ControlNetSource);
        Assert.False(clip.ClipLengthFromAudio);
        Assert.False(clip.ClipLengthFromControlNet);
        Assert.False(clip.ReuseAudio);
        Assert.Equal(0, stage.ClipStageIndex);
        Assert.Equal(1280, spec.Width);
        Assert.Equal(720, spec.Height);
        Assert.Equal(97, clip.Frames);
    }

    [Fact]
    public void ParseConfig_ControlNetLength_PropagatesToClipAndDisablesAudioLength()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    audioSource: Constants.AudioSourceUpload,
                    clipLengthFromAudio: true,
                    clipLengthFromControlNet: true)
            ]));
        WorkflowGenerator parser = BuildParser(json);

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        ClipSpec clip = Assert.Single(spec.Clips);
        Assert.False(clip.ClipLengthFromAudio);
        Assert.True(clip.ClipLengthFromControlNet);
    }

    [Fact]
    public void ParseConfig_RegisteredRootParams_OverrideJsonRootDimensions()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(VideoStagesExtension.RootWidth, 1536);
        input.Set(VideoStagesExtension.RootHeight, 864);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        ClipSpec clip = Assert.Single(spec.Clips);
        Assert.Equal(1536, spec.Width);
        Assert.Equal(864, spec.Height);
        Assert.Equal(97, clip.Frames);
    }

    [Fact]
    public void ParseConfig_RegisteredRootFps_OverridesCoreVideoFpsForClipDurationFrames()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(VideoStagesExtension.RootFPS, 32);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        ClipSpec clip = Assert.Single(spec.Clips);
        Assert.Equal(129, clip.Frames);
        Assert.Equal(32, spec.FPS);
    }

    [Fact]
    public void ParseClips_StagesMissingModel_ThrowsUserError()
    {
        JObject brokenStage = MakeStage("");
        brokenStage["Model"] = "";

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip( stages: [brokenStage, MakeStage("model-a")])
        ));
        WorkflowGenerator parser = BuildParser(json);

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => VideoStagesSpecParser.Parse(parser));
        Assert.Contains("Clip 0 stage 0", ex.Message);
        Assert.Contains("'Model'", ex.Message);
    }

    [Fact]
    public void ParseClips_NonClipShape_ThrowsUserError()
    {
        string json = JsonConvert.SerializeObject(new JArray(new JObject
        {
            ["Model"] = "model-a"
        }));
        WorkflowGenerator parser = BuildParser(json);

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => VideoStagesSpecParser.Parse(parser));
        Assert.Contains("Entry 0 is not a clip object", ex.Message);
        Assert.Contains("'Stages' array", ex.Message);
    }

    [Fact]
    public void ParseClips_EmptyJson_ReturnsEmpty()
    {
        WorkflowGenerator parser = BuildParser("[]");
        Assert.Empty(VideoStagesSpecParser.Parse(parser).Clips);
        Assert.Empty(FlattenedActiveStages(parser));
    }

    [Fact]
    public void ParseClips_InvalidJson_ThrowsUserError()
    {
        WorkflowGenerator parser = BuildParser("not json at all");
        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => VideoStagesSpecParser.Parse(parser));
        Assert.Contains("Could not parse Video Stages JSON", ex.Message);
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
        WorkflowGenerator parser = BuildParser(json);

        IReadOnlyList<ClipSpec> clips = VideoStagesSpecParser.Parse(parser).Clips;

        Assert.Single(clips);
        Assert.Single(clips[0].ImageRefs);
        Assert.Equal("Base", clips[0].ImageRefs[0].Source);
    }

    [Fact]
    public void ParseConfig_PropagatesTopLevelDimensionsAndPerClipFrames()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 800,
            height: 600,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0),
                MakeClip(
                    stages: [MakeStage("model-b")],
                    duration: 2.0)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(800, spec.Width);
        Assert.Equal(600, spec.Height);
        Assert.Equal(2, spec.Clips.Count);
        Assert.Equal(97, spec.Clips[0].Frames);
        Assert.Equal(49, spec.Clips[1].Frames);
    }

    [Theory]
    [InlineData(10.0, 241)]
    [InlineData(21.5, 521)]
    public void ParseConfig_ClipDurationFrames_AreAlignedUpToEightPlusOne(double duration, int expectedFrames)
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: duration)
            ]));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 24);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        ClipSpec clip = Assert.Single(VideoStagesSpecParser.Parse(parser).Clips);

        Assert.Equal(expectedFrames, clip.Frames);
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
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec clip = VideoStagesSpecParser.Parse(parser).Clips.Single();
        Assert.Equal("Upload", clip.ImageRefs[0].Source);
        Assert.Equal("ref.png", clip.ImageRefs[0].UploadFileName);
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
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec clip = VideoStagesSpecParser.Parse(parser).Clips.Single();
        ImageRefSpec r = clip.ImageRefs[0];
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
        WorkflowGenerator parser = BuildParser(json);

        ImageRefSpec r = VideoStagesSpecParser.Parse(parser).Clips.Single().ImageRefs[0];
        Assert.Equal("data:image/png;base64,TkVTVA==", r.Data);
        Assert.Equal("nested.png", r.UploadFileName);
    }

    [Fact]
    public void ParseConfig_ClipExposesRefsAndStageNormalizedRefStrengths()
    {
        JObject stage = MakeStage("model-a");
        stage["refStrengths"] = new JArray(0.55, 0.66);
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [stage],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 9)])));

        WorkflowGenerator parser = BuildParser(json);

        ClipSpec clip = Assert.Single(VideoStagesSpecParser.Parse(parser).Clips);
        StageSpec flattened = Assert.Single(clip.Stages);
        Assert.Equal(2, clip.ImageRefs.Count);
        Assert.Equal(2, flattened.ImageRefStrengths.Count);
        Assert.Equal(0.55, flattened.ImageRefStrengths[0]);
        Assert.Equal(0.66, flattened.ImageRefStrengths[1]);
    }

    [Fact]
    public void ParseConfig_FlattenedStagesIncludeControlNetStrength()
    {
        JObject stage = MakeStage("model-a");
        stage["ControlNetStrength"] = 0.35;
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [stage])));

        WorkflowGenerator parser = BuildParser(json);

        StageSpec flattened = Assert.Single(FlattenedActiveStages(parser));
        Assert.Equal(0.35, flattened.ControlNetStrength);
    }

    [Fact]
    public void ParseConfig_PadsMissingRefStrengthsToMatchReferenceCount()
    {
        JObject stage = MakeStage("model-a");
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [stage],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 2)])));

        WorkflowGenerator parser = BuildParser(json);

        StageSpec flattened = Assert.Single(FlattenedActiveStages(parser));
        Assert.Equal(2, flattened.ImageRefStrengths.Count);
        Assert.All(
            flattened.ImageRefStrengths,
            strength => Assert.Equal(Constants.DefaultStageRefStrength, strength));
    }

    [Fact]
    public void ParseClips_WanClip_TrimsRefsToTwoAndNormalizesFrameSemantics()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();
        JObject wanStage = MakeStage(models.VideoModel.Name);
        wanStage["refStrengths"] = new JArray(0.4, 0.5, 0.6);
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [wanStage],
                refs:
                [
                    MakeRef("Base", frame: 5, fromEnd: true),
                    MakeRef("Refiner", frame: 9, fromEnd: false),
                    MakeRef("Base", frame: 2),
                ])));

        WorkflowGenerator parser = BuildParser(json);
        ClipSpec clip = VideoStagesSpecParser.Parse(parser).Clips.Single();

        Assert.Equal(2, clip.ImageRefs.Count);
        Assert.Equal(1, clip.ImageRefs[0].Frame);
        Assert.False(clip.ImageRefs[0].FromEnd);
        Assert.Equal(1, clip.ImageRefs[1].Frame);
        Assert.True(clip.ImageRefs[1].FromEnd);

        StageSpec stageSpec = Assert.Single(FlattenedActiveStages(parser));
        Assert.Equal(2, stageSpec.ImageRefStrengths.Count);
    }

    [Fact]
    public void ParseConfig_WidthZero_FallsBackToGlobal()
    {
        JObject root = MakeRootConfig(
            width: 0,
            height: 720,
            clips: [MakeClip(stages: [MakeStage("model-a")])]);
        string json = JsonConvert.SerializeObject(root);
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.Width, 1024);
        input.Set(T2IParamTypes.Height, 768);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(1024, spec.Width);
        Assert.Equal(720, spec.Height);
    }

    [Fact]
    public void ParseConfig_HeightZero_FallsBackToGlobal()
    {
        JObject root = MakeRootConfig(
            width: 1280,
            height: 0,
            clips: [MakeClip(stages: [MakeStage("model-a")])]);
        string json = JsonConvert.SerializeObject(root);
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.Width, 1024);
        input.Set(T2IParamTypes.Height, 768);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(1280, spec.Width);
        Assert.Equal(768, spec.Height);
    }

    [Fact]
    public void ParseConfig_FpsZero_FallsBackToVideoFps()
    {
        JObject root = new()
        {
            ["Width"] = 1280,
            ["Height"] = 720,
            ["FPS"] = 0,
            ["Clips"] = new JArray(MakeClip(stages: [MakeStage("model-a")])),
        };
        string json = JsonConvert.SerializeObject(root);
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 30);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(30, spec.FPS);
    }

    [Fact]
    public void ParseConfig_FpsMissing_FallsBackToVideoFps()
    {
        JObject root = MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [MakeClip(stages: [MakeStage("model-a")])]);
        string json = JsonConvert.SerializeObject(root);
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.VideoFPS, 30);
        WorkflowGenerator generator = new() { UserInput = input };
        WorkflowGenerator parser = generator;

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(30, spec.FPS);
    }

    [Fact]
    public void ParseConfig_AllDimensionsAndFpsMissing_FallsBackToGlobalDefaults()
    {
        JObject root = new()
        {
            ["Clips"] = new JArray(MakeClip(stages: [MakeStage("model-a")])),
        };
        string json = JsonConvert.SerializeObject(root);
        WorkflowGenerator parser = BuildParser(json);

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        // GetImageWidth/Height default to 512 when unset.
        Assert.Equal(512, spec.Width);
        Assert.Equal(512, spec.Height);
        // FPS chain falls through to the hardcoded 24 default.
        Assert.Equal(24, spec.FPS);
    }
}
