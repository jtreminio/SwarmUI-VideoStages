using System.Globalization;
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
        JArray icLoras = null,
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
            ["SaveAudioTrack"] = saveAudioTrack,
            ["ClipLengthFromAudio"] = clipLengthFromAudio,
            ["ClipLengthFromControlNet"] = clipLengthFromControlNet,
            ["ReuseAudio"] = reuseAudio,
            ["Refs"] = new JArray(refs ?? []),
            ["Stages"] = new JArray(stages),
        };
        if (icLoras is not null)
        {
            clip["IcLoras"] = icLoras;
        }
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
        SetVideoStagesConfig(input, json);
        return input;
    }

    private static WorkflowGenerator BuildParser(string json)
    {
        T2IParamInput input = BuildInputWithJson(json);
        return new() { UserInput = input };
    }

    // Prose and prompt windows now arrive via <videoclip...> prompt tags. This sets the prompt and
    // runs the late special logic so the authoring tags are normalized into markers the parser reads.
    private static WorkflowGenerator BuildParser(string json, string prompt)
    {
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.ApplyLateSpecialLogic();
        return new() { UserInput = input };
    }

    // A clip-level prompt window authoring tag: <videoclip[clip]:start-end>text (seconds).
    private static string ClipWindowTag(string prompt, double start, double duration, int clip = 0)
    {
        string s = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string e = (start + duration).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"<videoclip[{clip}]:{s}-{e}>{prompt}";
    }

    private static List<StageSpec> FlattenedActiveStages(WorkflowGenerator parser) =>
        [.. VideoStagesSpecParser.Parse(parser).Clips.SelectMany(c => c.Stages)];

    private static ClipSpec ParseSingleClip(JObject clip)
    {
        string json = JsonConvert.SerializeObject(new JArray(clip));
        return Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json)).Clips);
    }

    [Fact]
    public void ParseClips_PromptWindows_ParsesStartAndDuration()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 4.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));
        string prompt =
            ClipWindowTag("a red car", start: 0.5, duration: 1.0)
            + " " + ClipWindowTag("a blue boat", start: 2.0, duration: 1.0);

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json, prompt)).Clips);

        Assert.Equal(2, parsed.PromptWindows.Count);
        Assert.Equal("a red car", parsed.PromptWindows[0].Prompt);
        Assert.Equal(0.5, parsed.PromptWindows[0].Start);
        Assert.Equal(1.0, parsed.PromptWindows[0].Duration);
        Assert.Equal("a blue boat", parsed.PromptWindows[1].Prompt);
        Assert.Equal(2.0, parsed.PromptWindows[1].Start);
    }

    [Fact]
    public void ParseClips_PromptWindows_SortsByStartAndDropsNonPositiveDuration()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 4.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));
        // The middle tag has end == start (0 duration): not a valid window, so its marker is dropped.
        // It carries no trailing prose so nothing bleeds into a neighboring window's text.
        string prompt =
            ClipWindowTag("late", start: 3.0, duration: 0.5)
            + " <videoclip[0]:1-1> "
            + ClipWindowTag("early", start: 0.0, duration: 0.5);

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json, prompt)).Clips);

        Assert.Equal(2, parsed.PromptWindows.Count);
        Assert.Equal("early", parsed.PromptWindows[0].Prompt);
        Assert.Equal("late", parsed.PromptWindows[1].Prompt);
    }

    [Fact]
    public void ParseClips_NoPromptWindows_YieldsEmptyList()
    {
        ClipSpec parsed = ParseSingleClip(MakeClip(stages: [MakeStage("model-a")], duration: 4.0));
        Assert.Empty(parsed.PromptWindows);
    }

    [Fact]
    public void ParseClips_StageScopedWindow_IsInvalidAndIgnored()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")], duration: 8.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));
        // Windows are clip-level only; the [0,1] range tag is invalid and produces no window.
        string prompt =
            ClipWindowTag("clip wide", start: 4.0, duration: 1.0)
            + " <videoclip[0,1]:0-1>stage one";

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json, prompt)).Clips);

        PromptWindowSpec clipWindow = Assert.Single(parsed.PromptWindows);
        Assert.Equal("clip wide", clipWindow.Prompt);
    }

    [Fact]
    public void ParseClips_ClipWindows_TileSortedByStart()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 8.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));
        // Two clip-level windows authored out of order; the executor tiles them sorted by start.
        string prompt =
            ClipWindowTag("clip late", start: 4.0, duration: 1.0)
            + " " + ClipWindowTag("clip early", start: 0.0, duration: 1.0);

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json, prompt)).Clips);

        List<(string Prompt, double Seconds)> tiled =
            VideoStages.LTX2.PromptWindowTiler.TilePromptWindows(parsed.PromptWindows, clipSeconds: 8.0);

        Assert.Equal("clip early", tiled[0].Prompt);
        Assert.Contains(tiled, segment => segment.Prompt == "clip late");
        Assert.True(
            tiled.FindIndex(s => s.Prompt == "clip early") < tiled.FindIndex(s => s.Prompt == "clip late"),
            "Clip windows must tile sorted by start (early before late).");
    }

    [Fact]
    public void ParseClips_ScalarOverride_MutatesClipField()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 3.0, audioSource: Constants.AudioSourceNative);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(
            VideoStagesSpecParser.Parse(BuildParser(json, "<videoclip[0,audiosource]:CustomAudio>")).Clips);

        Assert.Equal("CustomAudio", parsed.AudioSource);
    }

    [Fact]
    public void ParseClips_StageScalarOverride_MutatesStageField()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a", steps: 8)], duration: 3.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(
            VideoStagesSpecParser.Parse(BuildParser(json, "<videoclip[0,0,steps]:20>")).Clips);

        Assert.Equal(20, parsed.Stages[0].Steps);
    }

    [Fact]
    public void ParseClips_NumericOverrides_AreCultureInvariant()
    {
        // Override VALUES are always invariant (the tag/JSON grammar is invariant). On a comma-decimal locale
        // a culture-sensitive re-parse of "5.5" yields 55 (AllowThousands treats '.' as a group separator),
        // silently 10x-corrupting duration/cfgscale/etc. This locks in the invariant round-trip.
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            JObject clip = MakeClip(stages: [MakeStage("model-a", cfg: 1)], duration: 3.0);
            string json = JsonConvert.SerializeObject(new JArray(clip));
            string prompt = "<videoclip[0,duration]:5.5> <videoclip[0,0,cfgscale]:5.5>";

            VideoStagesSpec spec = VideoStagesSpecParser.Parse(BuildParser(json, prompt));
            ClipSpec parsed = Assert.Single(spec.Clips);

            // Stage double override read as 5.5, not 55.
            Assert.Equal(5.5, parsed.Stages[0].CfgScale);
            // Clip duration 5.5s @ 24fps -> aligned frame count of 137 (55s would be ~1321 frames).
            Assert.Equal(137, parsed.Frames);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void ParseClips_UnknownOverrideField_IsIgnoredWithoutThrowing()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 3.0, audioSource: Constants.AudioSourceNative);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(
            VideoStagesSpecParser.Parse(BuildParser(json, "<videoclip[0,bogusfield]:whatever>")).Clips);

        // Parse still succeeds; the unknown field left the clip untouched.
        Assert.Equal(Constants.AudioSourceNative, parsed.AudioSource);
    }

    [Fact]
    public void ParseClips_OutOfRangeOverrideIndex_IsIgnoredWithoutThrowing()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 3.0, audioSource: Constants.AudioSourceNative);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        // Clip 5 and stage 5 do not exist; both overrides are silently dropped.
        IReadOnlyList<ClipSpec> clips = VideoStagesSpecParser.Parse(
            BuildParser(json, "<videoclip[5,audiosource]:X> <videoclip[0,5,steps]:99>")).Clips;

        ClipSpec parsed = Assert.Single(clips);
        Assert.Equal(Constants.AudioSourceNative, parsed.AudioSource);
    }


    [Fact]
    public void ParseClips_ClipShape_PopulatesPerClipFields()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 12, fromEnd: true)],
                duration: 4.0,
                icLoras: new JArray(new JObject
                {
                    ["Lora"] = "clip-lora",
                    ["Preset"] = "deblur",
                    ["Stage"] = 0,
                    ["Source"] = Constants.ControlNetSourceTwo,
                    ["Strength"] = 0.7,
                    ["AttentionStrength"] = 0.4,
                    ["ControlType"] = Constants.IcLoraControlCanny,
                }),
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
        IcLoraSpec entry = Assert.Single(clips[0].IcLoras);
        Assert.Equal("clip-lora", entry.Lora);
        Assert.Equal("deblur", entry.Preset);
        Assert.Equal(0, entry.Stage);
        Assert.Equal(Constants.ControlNetSourceTwo, entry.Source);
        Assert.Equal(0.7, entry.Strength);
        Assert.Equal(0.4, entry.AttentionStrength);
        Assert.Equal(Constants.IcLoraControlCanny, entry.ControlType);
        Assert.Equal(Constants.ControlNetSourceTwo, clips[0].PrimarySlotEntry?.Source);
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

    // The ONE legacy-shape test: old saved configs carry ControlNetLora + ControlNetSource on the
    // clip itself; the parser maps them to a single default-strength IC-LoRA entry. Every other
    // fixture authors the shipping IcLoras[] shape.
    [Fact]
    public void ParseClips_LegacyControlNetFields_MapToSingleIcLoraEntry()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["ControlNetLora"] = "legacy-lora";
        clip["ControlNetSource"] = Constants.ControlNetSourceTwo;

        ClipSpec parsed = ParseSingleClip(clip);

        IcLoraSpec entry = Assert.Single(parsed.IcLoras);
        Assert.Equal("legacy-lora", entry.Lora);
        Assert.Equal(Constants.ControlNetSourceTwo, entry.Source);
        Assert.Equal(1, entry.Strength);
        Assert.Equal(1, entry.AttentionStrength);
        Assert.Equal(Constants.IcLoraControlNone, entry.ControlType);
        Assert.Null(entry.Video);
        Assert.Equal(Constants.ControlNetSourceTwo, parsed.PrimarySlotEntry?.Source);
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
        Assert.Empty(clip.IcLoras);
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
    public void ParseConfig_JsonRootDimensions_AreAuthoritative()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(
            width: 1536,
            height: 864,
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
        Assert.Equal(1536, spec.Width);
        Assert.Equal(864, spec.Height);
        Assert.Equal(97, clip.Frames);
    }

    [Fact]
    public void ParseConfig_JsonRootFps_OverridesCoreVideoFpsForClipDurationFrames()
    {
        JObject config = MakeRootConfig(
            width: 1280,
            height: 720,
            clips: [
                MakeClip(
                    stages: [MakeStage("model-a")],
                    duration: 4.0)
            ]);
        config["FPS"] = 32;
        string json = JsonConvert.SerializeObject(config);
        T2IParamInput input = BuildInputWithJson(json);
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
    public void ParseClips_IcLoraStageBeyondStageList_ThrowsUserError()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                icLoras: new JArray(new JObject
                {
                    ["Lora"] = "clip-lora",
                    ["Stage"] = 2,
                    ["Source"] = Constants.IcLoraSourceUpload,
                }))
        ));
        WorkflowGenerator parser = BuildParser(json);

        SwarmUserErrorException ex = Assert.Throws<SwarmUserErrorException>(
            () => VideoStagesSpecParser.Parse(parser));
        Assert.Contains("apply on stage 2", ex.Message);
        Assert.Contains("All stages", ex.Message);
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

    [Fact]
    public void ParseStage_Clip0Stage0_DefaultsToHardcodedFirstStageControl()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")])));
        WorkflowGenerator parser = BuildParser(json);

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(1.0, spec.Clips[0].Stages[0].Control);
    }

    [Fact]
    public void ParseStage_Clip0Stage0_RefineSourceVideoMode_ForcesControlToZero()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")])));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0x00], MediaType.VideoMp4));
        WorkflowGenerator parser = new() { UserInput = input };

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(0.0, spec.Clips[0].Stages[0].Control);
        Assert.Equal(1.0, spec.Clips[0].Stages[1].Control);
    }

    [Fact]
    public void ParseStage_NonZeroClip_RefineSourceVideoMode_KeepsHardcodedFirstStageControl()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a")]),
            MakeClip(stages: [MakeStage("model-b")])));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0x00], MediaType.VideoMp4));
        WorkflowGenerator parser = new() { UserInput = input };

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(0.0, spec.Clips[0].Stages[0].Control);
        Assert.Equal(1.0, spec.Clips[1].Stages[0].Control);
    }

    [Fact]
    public void ParseStage_RefineSkipStagesTwo_ZeroesFirstTwoStagesOfClipZero()
    {
        JObject stage1 = MakeStage("model-b");
        stage1["Control"] = 0.4;
        stage1["Upscale"] = 1.5;
        JObject stage2 = MakeStage("model-c");
        stage2["Control"] = 0.6;

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), stage1, stage2])));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0x00], MediaType.VideoMp4));
        input.Set(VideoStagesExtension.RefineSkipStages, 2);
        WorkflowGenerator parser = new() { UserInput = input };

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(0.0, spec.Clips[0].Stages[0].Control);
        Assert.Equal(0.0, spec.Clips[0].Stages[1].Control);
        Assert.Equal(1.0, spec.Clips[0].Stages[1].Upscale);
        Assert.Equal(0.6, spec.Clips[0].Stages[2].Control);
    }

    [Fact]
    public void ParseStage_RefineSkipStages_DefaultsToOneWhenParamUnset()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")])));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0x00], MediaType.VideoMp4));
        WorkflowGenerator parser = new() { UserInput = input };

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(0.0, spec.Clips[0].Stages[0].Control);
        Assert.Equal(1.0, spec.Clips[0].Stages[1].Control);
    }

    [Fact]
    public void ParseStage_RefineSkipStages_IgnoredWhenRefineModeOff()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")])));
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(VideoStagesExtension.RefineSkipStages, 5);
        WorkflowGenerator parser = new() { UserInput = input };

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(1.0, spec.Clips[0].Stages[0].Control);
        Assert.Equal(1.0, spec.Clips[0].Stages[1].Control);
    }

    [Theory]
    [InlineData("cut", "cut")]
    [InlineData("continue", "continue")]
    [InlineData("crossfade", "crossfade")]
    [InlineData("Crossfade", "crossfade")]
    [InlineData("  CONTINUE ", "continue")]
    [InlineData("wipe", "cut")]
    [InlineData("", "cut")]
    public void ParseClips_BoundaryOut_NormalizesWithUnknownFallingBackToCut(string raw, string expected)
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["BoundaryOut"] = raw;
        string json = JsonConvert.SerializeObject(new JArray(clip));
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec parsed = VideoStagesSpecParser.Parse(parser).Clips.Single();

        Assert.Equal(expected, parsed.BoundaryOut);
    }

    [Fact]
    public void ParseClips_BoundaryOut_DefaultsCutWhenAbsent_BackCompat()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        string json = JsonConvert.SerializeObject(new JArray(clip));
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec parsed = VideoStagesSpecParser.Parse(parser).Clips.Single();

        Assert.Equal(Constants.BoundaryOutCut, parsed.BoundaryOut);
    }

    // No VideoFPS param and no top-level JSON FPS => the parser falls back to 24 fps, so seconds map to
    // frames at 24×.
    private static WorkflowGenerator BuildRefineParser(string json)
    {
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(VideoStagesExtension.RefineSourceVideo, new Image([0xDE, 0xAD, 0xBE, 0xEF], MediaType.VideoMp4));
        input.Set(VideoStagesExtension.RefineSkipStages, 0);
        return new() { UserInput = input };
    }

    private static JObject MakeRetake(double startSeconds, double lengthSeconds, double? strength = null)
    {
        JObject retake = new()
        {
            ["StartSeconds"] = startSeconds,
            ["LengthSeconds"] = lengthSeconds,
        };
        if (strength is not null)
        {
            retake["Strength"] = strength.Value;
        }
        return retake;
    }

    [Fact]
    public void ParseClips_Retake_ConvertsSecondsToFramesAtFpsAndAttachesToLastStage()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")]);
        clip["Retake"] = MakeRetake(startSeconds: 1.0, lengthSeconds: 2.0, strength: 0.6);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildRefineParser(json)).Clips);

        Assert.Null(parsed.Stages[0].RetakeWindow);
        RetakeWindowSpec retake = parsed.Stages[^1].RetakeWindow;
        Assert.NotNull(retake);
        Assert.Equal(24, retake.StartFrame);
        Assert.Equal(48, retake.LengthFrames);
        Assert.Equal(0.6, retake.Strength, 6);
    }

    [Fact]
    public void ParseClips_Retake_DefaultsStrengthToOneWhenAbsent()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["Retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 1.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildRefineParser(json)).Clips);

        RetakeWindowSpec retake = parsed.Stages[^1].RetakeWindow;
        Assert.NotNull(retake);
        Assert.Equal(0, retake.StartFrame);
        Assert.Equal(24, retake.LengthFrames);
        Assert.Equal(1.0, retake.Strength, 6);
    }

    [Fact]
    public void ParseClips_Retake_ClampsStrengthToUnitRange()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["Retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 1.0, strength: 5.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildRefineParser(json)).Clips);

        Assert.Equal(1.0, parsed.Stages[^1].RetakeWindow.Strength, 6);
    }

    [Fact]
    public void ParseClips_Retake_NullWhenNotRefineMode()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["Retake"] = MakeRetake(startSeconds: 1.0, lengthSeconds: 2.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        // BuildParser does NOT set a refine-source video, so retake never activates.
        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json)).Clips);

        Assert.All(parsed.Stages, stage => Assert.Null(stage.RetakeWindow));
    }

    [Theory]
    [InlineData(0.0, 0.0)]   // zero length
    [InlineData(1.0, -1.0)]  // negative length
    [InlineData(-1.0, 2.0)]  // negative start
    public void ParseClips_Retake_NullWhenInvalidWindow(double startSeconds, double lengthSeconds)
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["Retake"] = MakeRetake(startSeconds, lengthSeconds);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildRefineParser(json)).Clips);

        Assert.All(parsed.Stages, stage => Assert.Null(stage.RetakeWindow));
    }

    [Fact]
    public void ParseClips_Retake_NullWhenSubFrameLengthRoundsToZero()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        // 0.01s at 24 fps => round(0.24) = 0 frames => disabled.
        clip["Retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 0.01);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildRefineParser(json)).Clips);

        Assert.Null(parsed.Stages[^1].RetakeWindow);
    }

    [Fact]
    public void ParseClips_Retake_AbsentLeavesStagesUntouched()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildRefineParser(json)).Clips);

        Assert.Null(parsed.Stages[^1].RetakeWindow);
    }

    private static JObject MakeAudioSegment(
        double startSeconds,
        double trimStartSeconds,
        double lengthSeconds,
        JObject source = null)
    {
        JObject seg = new()
        {
            ["StartSeconds"] = startSeconds,
            ["TrimStartSeconds"] = trimStartSeconds,
            ["LengthSeconds"] = lengthSeconds,
        };
        if (source is not null)
        {
            seg["Source"] = source;
        }
        return seg;
    }

    [Fact]
    public void ParseClips_AudioSegments_AbsentIsEmpty()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        ClipSpec parsed = ParseSingleClip(clip);
        Assert.Empty(parsed.AudioSegments);
    }

    [Fact]
    public void ParseClips_AudioSegments_ParsesClampsSortsAndRounds()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 10.0);
        clip["AudioSegments"] = new JArray(
            MakeAudioSegment(4.0, 0.0, 1.0, MakeUploadedAudio()),
            // Length overruns the clip -> clamped to (10 - 8) = 2.
            MakeAudioSegment(8.0, 0.5, 5.0, MakeUploadedAudio()),
            MakeAudioSegment(1.0, 0.0, 2.0, MakeUploadedAudio()));

        ClipSpec parsed = ParseSingleClip(clip);

        Assert.Equal(3, parsed.AudioSegments.Count);
        // Sorted by start.
        Assert.Equal(new[] { 1.0, 4.0, 8.0 }, parsed.AudioSegments.Select(s => s.StartSeconds));
        Assert.Equal(2.0, parsed.AudioSegments[2].LengthSeconds);
        Assert.All(parsed.AudioSegments, s => Assert.NotNull(s.Source));
    }

    [Fact]
    public void ParseClips_AudioSegments_AcceptsAceStepFunSourceRef()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 10.0);
        JObject seg = MakeAudioSegment(1.0, 0.0, 2.0);
        seg["Source"] = "audio2";
        clip["AudioSegments"] = new JArray(seg);

        ClipSpec parsed = ParseSingleClip(clip);

        AudioSegmentSpec parsedSeg = Assert.Single(parsed.AudioSegments);
        Assert.Null(parsedSeg.Source);
        Assert.Equal("audio2", parsedSeg.AceStepFunSource);
    }

    [Fact]
    public void ParseClips_AudioSegments_DropsUnknownStringSource()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 10.0);
        JObject seg = MakeAudioSegment(1.0, 0.0, 2.0);
        seg["Source"] = "not-an-audio-ref";
        clip["AudioSegments"] = new JArray(seg);

        Assert.Empty(ParseSingleClip(clip).AudioSegments);
    }

    [Fact]
    public void ParseClips_AudioSegments_DropsSourcelessAndInvalidLength()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 10.0);
        clip["AudioSegments"] = new JArray(
            MakeAudioSegment(1.0, 0.0, 2.0, MakeUploadedAudio()),
            MakeAudioSegment(2.0, 0.0, 2.0), // no source -> dropped
            MakeAudioSegment(3.0, 0.0, 0.0, MakeUploadedAudio())); // non-positive length -> dropped

        ClipSpec parsed = ParseSingleClip(clip);

        AudioSegmentSpec only = Assert.Single(parsed.AudioSegments);
        Assert.Equal(1.0, only.StartSeconds);
        Assert.Equal(2.0, only.LengthSeconds);
    }
}
