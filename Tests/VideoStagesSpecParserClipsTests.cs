using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class VideoStagesSpecParserClipsTests
{
    // Parser tests omit ImageReference and use lightweight sampling defaults.
    private static JObject MakeStage(string model, double cfg = 1, int steps = 8)
    {
        return new JObject
        {
            ["model"] = model,
            ["steps"] = steps,
            ["cfgScale"] = cfg,
            ["sampler"] = "euler",
            ["scheduler"] = "normal",
            ["control"] = 1,
            ["upscale"] = 1,
            ["upscaleMethod"] = "pixel-lanczos",
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
            ["skipped"] = skipped,
            ["duration"] = duration,
            ["audioSource"] = audioSource,
            ["saveAudioTrack"] = saveAudioTrack,
            ["clipLengthFromAudio"] = clipLengthFromAudio,
            ["clipLengthFromControlNet"] = clipLengthFromControlNet,
            ["reuseAudio"] = reuseAudio,
            ["refs"] = new JArray(refs ?? []),
            ["stages"] = new JArray(stages),
        };
        if (icLoras is not null)
        {
            clip["icLoras"] = icLoras;
        }
        if (uploadedAudio is not null)
        {
            clip["uploadedAudio"] = uploadedAudio;
        }
        return clip;
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

    // ApplyLateSpecialLogic converts authoring tags into markers consumed by the parser.
    private static WorkflowGenerator BuildParser(string json, string prompt)
    {
        T2IParamInput input = BuildInputWithJson(json);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.ApplyLateSpecialLogic();
        return new() { UserInput = input };
    }

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

    [Theory]
    [InlineData(null, ReferenceFramingMode.Crop)]
    [InlineData("crop", ReferenceFramingMode.Crop)]
    [InlineData("stretch", ReferenceFramingMode.Stretch)]
    [InlineData("fit", ReferenceFramingMode.Fit)]
    [InlineData("fit-green", ReferenceFramingMode.FitGreen)]
    [InlineData("future", ReferenceFramingMode.Crop)]
    public void ParseClips_ReferenceFraming_IsTypedAndDefaultsToCrop(
        string raw,
        ReferenceFramingMode expected)
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        if (raw is not null)
        {
            clip["refFraming"] = raw;
        }

        Assert.Equal(expected, ParseSingleClip(clip).ReferenceFraming);
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
        // The zero-duration marker has no trailing prose to attach to another window.
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
        string prompt =
            ClipWindowTag("clip late", start: 4.0, duration: 1.0)
            + " " + ClipWindowTag("clip early", start: 0.0, duration: 1.0);

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json, prompt)).Clips);

        var tiled = PromptRelayPlanCompiler.Tile(
            parsed.PromptWindows.Select(window => new PromptWindowPlan(
                window.Prompt,
                window.Start,
                window.Duration,
                window.Start + window.Duration)),
            clipSeconds: 8.0);

        Assert.Equal("clip early", tiled[0].Prompt);
        Assert.Contains(tiled, segment => segment.Prompt == "clip late");
        PromptRelaySegmentPlan[] tiledArray = tiled.ToArray();
        Assert.True(
            Array.FindIndex(tiledArray, s => s.Prompt == "clip early")
                < Array.FindIndex(tiledArray, s => s.Prompt == "clip late"),
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
    public void ParseClips_StageOverride_CanReviveRawSkippedStage()
    {
        JObject skipped = MakeStage("model-b");
        skipped["skipped"] = true;
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), skipped])));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(
            json,
            "<videoclip[0,1,skipped]:false>")).Clips);

        Assert.Equal(["model-a", "model-b"], parsed.Stages.Select(stage => stage.Model));
    }

    [Fact]
    public void ParseClips_NumericOverrides_AreCultureInvariant()
    {
        // Culture-sensitive parsing can interpret "5.5" as 55 on comma-decimal locales.
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

            Assert.Equal(5.5, parsed.Stages[0].CfgScale);
            Assert.Equal(133, parsed.Frames);
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
        WorkflowGenerator parser =
            BuildParser(json, "<videoclip[0,bogusfield]:whatever>");

        ClipSpec parsed = Assert.Single(
            VideoStagesSpecParser.Parse(parser).Clips);

        Assert.Equal(Constants.AudioSourceNative, parsed.AudioSource);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains(
                "ignoring unknown or non-overridable clip 0 field 'bogusfield'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ParseClips_OutOfRangeOverrideIndex_IsIgnoredWithoutThrowing()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 3.0, audioSource: Constants.AudioSourceNative);
        string json = JsonConvert.SerializeObject(new JArray(clip));

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
                    ["lora"] = "clip-lora",
                    ["preset"] = "deblur",
                    ["stage"] = 0,
                    ["driveSource"] = Constants.ControlNetSourceTwo,
                    ["driveData"] = nameof(IcLoraDriveData.Visual),
                    ["strength"] = 0.7,
                    ["attentionStrength"] = 0.4,
                    ["controlType"] = Constants.IcLoraControlCanny,
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
        Assert.Equal(Constants.ControlNetSourceTwo, entry.DriveSource);
        Assert.Equal(IcLoraDriveData.Visual, entry.DriveData);
        Assert.Equal(0.7, entry.Strength);
        Assert.Equal(0.4, entry.AttentionStrength);
        Assert.Equal(Constants.IcLoraControlCanny, entry.ControlType);
        Assert.True(clips[0].SaveAudioTrack);
        Assert.True(clips[0].ClipLengthFromAudio);
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
    public void ParseClips_IcLoraReadsDriveMedia()
    {
        JObject icLora = new()
        {
            ["lora"] = "lipdub.safetensors",
            ["preset"] = "lipdub",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["driveData"] = nameof(IcLoraDriveData.Audio),
            ["driveMediaKinds"] = new JArray("audio", "video"),
            ["controlType"] = Constants.IcLoraControlNone,
            ["driveMedia"] = new JObject
            {
                ["data"] = "data:audio/wav;base64,QUJD",
                ["fileName"] = "target-voice.wav",
            },
        };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                icLoras: new JArray(icLora))));

        ClipSpec clip = Assert.Single(
            VideoStagesSpecParser.Parse(BuildParser(json)).Clips);
        IcLoraSpec parsed = Assert.Single(clip.IcLoras);

        Assert.Equal("lipdub", parsed.Preset);
        Assert.Equal(Constants.IcLoraSourceUpload, parsed.DriveSource);
        Assert.Equal(IcLoraDriveData.Audio, parsed.DriveData);
        Assert.Equal(["audio", "video"], parsed.DriveMediaKinds);
        Assert.NotNull(parsed.DriveMedia);
        Assert.Equal("data:audio/wav;base64,QUJD", parsed.DriveMedia.Data);
        Assert.Equal("target-voice.wav", parsed.DriveMedia.FileName);
    }

    [Fact]
    public void ParseClips_IcLoraDoesNotInferMissingDriveDataFromPresetOrUpload()
    {
        JObject entry = new()
        {
            ["lora"] = "lipdub.safetensors",
            ["preset"] = "lipdub",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["driveMedia"] = new JObject
            {
                ["data"] = "data:audio/wav;base64,QUJD",
                ["fileName"] = "voice.wav",
            },
        };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                icLoras: new JArray(entry))));

        IcLoraSpec parsed = Assert.Single(
            Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json)).Clips).IcLoras);

        Assert.Equal(IcLoraDriveData.None, parsed.DriveData);
        Assert.Null(parsed.DriveMediaKinds);
    }

    [Fact]
    public void ParseClips_IcLoraDefaultsMalformedDriveDataWithWarning()
    {
        JObject entry = new()
        {
            ["lora"] = "adapter.safetensors",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["driveData"] = "future-stream",
        };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                icLoras: new JArray(entry))));

        WorkflowGenerator parser = BuildParser(json);
        IcLoraSpec parsed = Assert.Single(
            Assert.Single(VideoStagesSpecParser.Parse(parser).Clips).IcLoras);

        Assert.Equal(IcLoraDriveData.None, parsed.DriveData);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("unsupported DriveData 'future-stream'"));
    }

    [Fact]
    public void ParseClips_IcLoraDefaultsNonArrayDriveMediaKindsWithWarning()
    {
        JObject entry = new()
        {
            ["lora"] = "adapter.safetensors",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["driveData"] = nameof(IcLoraDriveData.Visual),
            ["driveMediaKinds"] = "image",
        };
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                icLoras: new JArray(entry))));

        WorkflowGenerator parser = BuildParser(json);
        IcLoraSpec parsed = Assert.Single(
            Assert.Single(VideoStagesSpecParser.Parse(parser).Clips).IcLoras);

        Assert.Null(parsed.DriveMediaKinds);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("DriveMediaKinds must be an array"));
    }

    [Fact]
    public void ParseClips_IcLoraDropsInvalidAndDuplicateDriveMediaKindsWithWarnings()
    {
        JObject entry = new()
        {
            ["lora"] = "adapter.safetensors",
            ["driveSource"] = Constants.IcLoraSourceUpload,
            ["driveData"] = nameof(IcLoraDriveData.Visual),
            ["driveMediaKinds"] = new JArray(123, "Image", "image", "future"),
        };
        WorkflowGenerator parser = BuildParser(JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                icLoras: new JArray(entry)))));

        IcLoraSpec parsed = Assert.Single(
            Assert.Single(VideoStagesSpecParser.Parse(parser).Clips).IcLoras);
        List<string> warnings =
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]);

        Assert.Equal(["image"], parsed.DriveMediaKinds);
        Assert.Equal(3, warnings.Count(warning => warning.Contains("DriveMediaKinds")));
        Assert.Contains(warnings, warning => warning.Contains("repeats 'image'"));
    }

    [Fact]
    public void ParseClips_LegacyControlNetFields_AreIgnored()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["ControlNetLora"] = "legacy-lora";
        clip["ControlNetSource"] = Constants.ControlNetSourceTwo;

        ClipSpec parsed = ParseSingleClip(clip);

        Assert.Empty(parsed.IcLoras);
    }

    [Fact]
    public void ParseClips_LegacyControlNetPromptOverrides_AreIgnored()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(
            BuildParser(
                json,
                "<videoclip[0,controlnetlora]:legacy-lora> "
                + "<videoclip[0,controlnetsource]:ControlNet 2>")).Clips);

        Assert.Empty(parsed.IcLoras);
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
    public void ReadDocument_GroupToggleOff_YieldsNoRootDimensionsOrEntries()
    {
        string json = JsonConvert.SerializeObject(MakeRootConfig(800, 600));
        T2IParamInput enabled = BuildInputWithJson(json);
        VideoStagesJsonDocument enabledDocument =
            VideoStagesJsonReader.ReadDocument(new() { UserInput = enabled });
        Assert.Equal(800, enabledDocument.Width);
        Assert.Equal(600, enabledDocument.Height);

        T2IParamInput disabled = BuildInputWithJson(json);
        disabled.Remove(VideoStagesExtension.Enabled);

        VideoStagesJsonDocument document =
            VideoStagesJsonReader.ReadDocument(new() { UserInput = disabled });

        Assert.Null(document.Width);
        Assert.Null(document.Height);
        Assert.Null(document.Fps);
        Assert.Empty(document.Entries);
    }

    [Fact]
    public void ParseClips_PerClipUploadedAudio_IsParsed()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                audioSource: Constants.AudioSourceUpload,
                uploadedAudio: UploadedAudio(fileName: "first.wav")),
            MakeClip(
                stages: [MakeStage("model-b")],
                audioSource: Constants.AudioSourceUpload,
                uploadedAudio: UploadedAudio(fileName: "second.wav"))
        ));
        WorkflowGenerator parser = BuildParser(json);

        IReadOnlyList<ClipSpec> clips = VideoStagesSpecParser.Parse(parser).Clips;

        Assert.Equal(2, clips.Count);
        Assert.Equal("first.wav", clips[0].UploadedAudio.FileName);
        Assert.Equal("second.wav", clips[1].UploadedAudio.FileName);

        AudioFile firstAudio = EmbeddedMediaMaterializer.MaterializeAudio(
            parser,
            clips[0].UploadedAudio);
        AudioFile secondAudio = EmbeddedMediaMaterializer.MaterializeAudio(
            parser,
            clips[1].UploadedAudio);
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
                    ["data"] = "inputs/_comfy1/clip_part02.wav",
                    ["fileName"] = "clip_part02.wav",
                })));
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec clip = VideoStagesSpecParser.Parse(parser).Clips.Single();

        Assert.Equal("inputs/_comfy1/clip_part02.wav", clip.UploadedAudio.Data);

        AudioFile audio = EmbeddedMediaMaterializer.MaterializeAudio(
            parser,
            clip.UploadedAudio);

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
        clipZeroStageZero["control"] = 0.25;
        JObject clipZeroStageOne = MakeStage("model-b");
        clipZeroStageOne["control"] = 0.35;
        JObject clipOneStageZero = MakeStage("model-c");
        clipOneStageZero["control"] = 0.45;

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
    public void ParseConfig_TruncatesAtFirstSkippedClipAndStage()
    {
        JObject skippedStage = MakeStage("model-skip");
        skippedStage["skipped"] = true;

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip( stages: [MakeStage("model-a"), skippedStage, MakeStage("model-late")]),
            MakeClip( stages: [MakeStage("model-skipped-clip")], skipped: true),
            MakeClip( stages: [MakeStage("model-c")])
        ));
        WorkflowGenerator parser = BuildParser(json);

        List<StageSpec> stages = FlattenedActiveStages(parser);

        Assert.Single(stages);
        Assert.Equal("model-a", stages[0].Model);
        ClipSpec firstClip = parser.GetVideoStagesSpec().Clips[0];
        Assert.Collection(
            firstClip.AuthoredStages,
            stage =>
            {
                Assert.Equal(0, stage.RawIndex);
                Assert.Equal("model-a", stage.Model);
                Assert.False(stage.Skipped);
            },
            stage =>
            {
                Assert.Equal(1, stage.RawIndex);
                Assert.Equal("model-skip", stage.Model);
                Assert.True(stage.Skipped);
            },
            stage =>
            {
                Assert.Equal(2, stage.RawIndex);
                Assert.Equal("model-late", stage.Model);
                Assert.True(stage.Skipped);
            });
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
    public void ParseConfig_ControlNetAndAudioLength_IntentsBothReachPlanning()
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
        Assert.True(clip.ClipLengthFromAudio);
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
        config["fps"] = 32;
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
    public void ParseClips_StagesMissingModel_WarnsAndSkipsTheStage()
    {
        JObject brokenStage = MakeStage("");
        brokenStage["model"] = "";

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip( stages: [brokenStage, MakeStage("model-a")])
        ));
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec clip = Assert.Single(VideoStagesSpecParser.Parse(parser).Clips);

        StageSpec stage = Assert.Single(clip.Stages);
        Assert.Equal("model-a", stage.Model);
        Assert.Equal(1, stage.ClipStageRawIndex);
        Assert.Equal(0, stage.ClipStageIndex);
        Assert.True(clip.AuthoredStages[0].Skipped);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("Clip 0 stage 0 has no model and was ignored"));
    }

    [Fact]
    public void ParseClips_AssignsFinalStageIdentityWhileCompactingMissingModels()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [
                MakeStage("model-a"),
                MakeStage(""),
                MakeStage("model-b"),
            ]),
            MakeClip(stages: [MakeStage("model-c")])));

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(BuildParser(json));

        Assert.Collection(
            spec.Clips[0].Stages,
            stage => Assert.Equal((0, 0, 0),
                (stage.Id, stage.ClipStageIndex, stage.ClipStageRawIndex)),
            stage => Assert.Equal((1, 1, 2),
                (stage.Id, stage.ClipStageIndex, stage.ClipStageRawIndex)));
        StageSpec secondClip = Assert.Single(spec.Clips[1].Stages);
        Assert.Equal((2, 0, 0),
            (secondClip.Id, secondClip.ClipStageIndex, secondClip.ClipStageRawIndex));
    }

    [Fact]
    public void ParseClips_IcLoraStageBeyondStageList_RemainsRawForArchitectureValidation()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(
                stages: [MakeStage("model-a")],
                icLoras: new JArray(new JObject
                {
                    ["lora"] = "clip-lora",
                    ["stage"] = 2,
                    ["driveSource"] = Constants.IcLoraSourceUpload,
                }))
        ));
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec clip = Assert.Single(VideoStagesSpecParser.Parse(parser).Clips);

        Assert.Equal(2, Assert.Single(clip.IcLoras).Stage);
    }

    [Fact]
    public void ParseClips_NonClipShape_WarnsAndSkipsTheEntry()
    {
        string json = JsonConvert.SerializeObject(new JArray(new JObject
        {
            ["model"] = "model-a"
        }));
        WorkflowGenerator parser = BuildParser(json);

        Assert.Empty(VideoStagesSpecParser.Parse(parser).Clips);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("Entry 0 has no stages array and was ignored"));
    }

    [Fact]
    public void ParseClips_DoesNotValidateMalformedEntriesAfterTheSkippedTail()
    {
        JObject skipped = new() { ["skipped"] = true };
        JObject malformed = new() { ["model"] = "unused" };
        WorkflowGenerator parser = BuildParser(JsonConvert.SerializeObject(
            new JArray(MakeClip(stages: [MakeStage("model-a")]), skipped, malformed)));

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Single(spec.Clips);
        Assert.False(parser.UserInput.ExtraMeta.ContainsKey("parser_warnings"));
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
        JObject brokenRef = new() { ["frame"] = 4 };
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
    [InlineData(21.5, 517)]
    public void ParseConfig_ClipDurationFrames_AreStructural(double duration, int expectedFrames)
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
            ["source"] = "Upload",
            ["uploadFileName"] = "ref.png",
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
            ["source"] = "Upload",
            ["uploadedImage"] = new JObject
            {
                ["data"] = imageData,
                ["fileName"] = "guide.png",
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
            ["source"] = "Upload",
            ["data"] = "data:image/png;base64,T1BQ",
            ["uploadedImage"] = new JObject
            {
                ["data"] = "data:image/png;base64,TkVTVA==",
                ["fileName"] = "nested.png",
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
        stage["controlNetStrength"] = 0.35;
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [stage])));

        WorkflowGenerator parser = BuildParser(json);

        StageSpec flattened = Assert.Single(FlattenedActiveStages(parser));
        Assert.Equal(0.35, flattened.ControlNetStrength);
    }

    [Fact]
    public void ParseConfig_FlattenedStagesIncludePerIcLoraStrengths()
    {
        JObject stage = MakeStage("model-a");
        stage["icLoraStrengths"] = new JArray(0.25, 0.75);
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [stage])));

        WorkflowGenerator parser = BuildParser(json);

        StageSpec flattened = Assert.Single(FlattenedActiveStages(parser));
        Assert.Equal([0.25, 0.75], flattened.IcLoraStrengths);
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
            ["width"] = 1280,
            ["height"] = 720,
            ["fps"] = 0,
            ["clips"] = new JArray(MakeClip(stages: [MakeStage("model-a")])),
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
            ["clips"] = new JArray(MakeClip(stages: [MakeStage("model-a")])),
        };
        string json = JsonConvert.SerializeObject(root);
        WorkflowGenerator parser = BuildParser(json);

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(512, spec.Width);
        Assert.Equal(512, spec.Height);
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
    public void ParseStage_InitVideoClipStage0_KeepsAuthoredControlAndUpscale()
    {
        JObject stage0 = MakeStage("model-a");
        stage0["control"] = 0.3;
        stage0["upscale"] = 2.0;
        stage0["upscaleMethod"] = "pixel-catmull";
        JObject clip = MakeClip(stages: [stage0], duration: 3.0);
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,QUJD",
            ["fileName"] = "footage.mp4",
            ["startSeconds"] = 0.0,
        };
        string json = JsonConvert.SerializeObject(new JArray(clip));
        WorkflowGenerator parser = BuildParser(json);

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(0.3, spec.Clips[0].Stages[0].Control);
        Assert.Equal(2.0, spec.Clips[0].Stages[0].Upscale);
        Assert.Equal("pixel-catmull", spec.Clips[0].Stages[0].UpscaleMethod);
    }

    [Fact]
    public void ParseClip_InitVideoStartBeyondFrameRange_WarnsAndDropsTheSource()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")], duration: 3.0);
        clip["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,QUJD",
            ["fileName"] = "footage.mp4",
            ["startSeconds"] = 1e20,
        };

        WorkflowGenerator parser = BuildParser(JsonConvert.SerializeObject(new JArray(clip)));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(parser).Clips);

        Assert.Null(parsed.InitVideo);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("InitVideo start")
                && warning.Contains("representable frame range"));
    }

    [Fact]
    public void ParseStage_Upscale_SnapsToQuarterSteps()
    {
        JObject stage1 = MakeStage("model-b");
        stage1["upscale"] = 1.3;
        JObject stage2 = MakeStage("model-c");
        stage2["upscale"] = 1.1;
        JObject stage3 = MakeStage("model-d");
        stage3["upscale"] = 0.3;

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip(stages: [MakeStage("model-a"), stage1, stage2, stage3])));
        WorkflowGenerator parser = new() { UserInput = BuildInputWithJson(json) };

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(parser);

        Assert.Equal(1.25, spec.Clips[0].Stages[1].Upscale);
        Assert.Equal(1.0, spec.Clips[0].Stages[2].Upscale);
        Assert.Equal(0.25, spec.Clips[0].Stages[3].Upscale);
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
        clip["boundaryOut"] = raw;
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
        Assert.False(parsed.BoundaryOutCarryAudio);
    }

    [Fact]
    public void ParseClips_BoundaryOutCarryAudio_PreservesAuthoredOptIn()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["boundaryOut"] = Constants.BoundaryOutCrossfade;
        clip["boundaryOutCarryAudio"] = true;
        string json = JsonConvert.SerializeObject(new JArray(clip));
        WorkflowGenerator parser = BuildParser(json);

        ClipSpec parsed = VideoStagesSpecParser.Parse(parser).Clips.Single();

        Assert.True(parsed.BoundaryOutCarryAudio);
    }

    // Adds a source video while leaving FPS unset to exercise the 24 FPS fallback.
    private static WorkflowGenerator BuildInitVideoParser(string json)
    {
        JArray clips = JArray.Parse(json);
        foreach (JObject clip in clips.Cast<JObject>())
        {
            clip["initVideo"] = new JObject
            {
                ["data"] = "data:video/mp4;base64,QUJD",
                ["fileName"] = "footage.mp4",
                ["startSeconds"] = 0.0,
            };
        }
        return BuildParser(JsonConvert.SerializeObject(clips));
    }

    private static JObject MakeRetake(double startSeconds, double lengthSeconds, double? strength = null)
    {
        JObject retake = new()
        {
            ["startSeconds"] = startSeconds,
            ["lengthSeconds"] = lengthSeconds,
        };
        if (strength is not null)
        {
            retake["strength"] = strength.Value;
        }
        return retake;
    }

    [Fact]
    public void ParseClips_DropsNegativeDuration()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["duration"] = -1;
        WorkflowGenerator parser = BuildInitVideoParser(
            JsonConvert.SerializeObject(new JArray(clip)));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(parser).Clips);

        Assert.Null(parsed.Frames);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("duration must be finite and non-negative")
                && warning.Contains("ignoring it"));
    }

    [Fact]
    public void ParseClips_DropsDurationBeyondTheRepresentableFrameRange()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["duration"] = int.MaxValue;
        string json = JsonConvert.SerializeObject(new JArray(clip));

        WorkflowGenerator parser = BuildInitVideoParser(json);

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(parser).Clips);

        Assert.Null(parsed.Frames);
        Assert.Contains(
            Assert.IsType<List<string>>(parser.UserInput.ExtraMeta["parser_warnings"]),
            warning => warning.Contains("duration at 24 fps exceeds")
                && warning.Contains("was ignored"));
    }

    [Fact]
    public void ParseClips_Retake_ConvertsSecondsToFramesAtFpsAndAttachesToLastStage()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a"), MakeStage("model-b")]);
        clip["retake"] = MakeRetake(startSeconds: 1.0, lengthSeconds: 1.5, strength: 0.6);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildInitVideoParser(json)).Clips);

        Assert.Null(parsed.Stages[0].RetakeWindow);
        RetakeWindowSpec retake = parsed.Stages[^1].RetakeWindow;
        Assert.NotNull(retake);
        Assert.Equal(24, retake.StartFrame);
        Assert.Equal(36, retake.LengthFrames);
        Assert.Equal(0.6, retake.Strength, 6);
    }

    [Fact]
    public void ParseClips_Retake_ReachingClipEndExtendsToStructuralFrameCount()
    {
        // Model-grid normalization occurs later, after architecture resolution.
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["duration"] = 1.05;
        clip["retake"] = MakeRetake(startSeconds: 0.5, lengthSeconds: 0.55);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildInitVideoParser(json)).Clips);

        RetakeWindowSpec retake = parsed.Stages[^1].RetakeWindow;
        Assert.NotNull(retake);
        Assert.Equal(12, retake.StartFrame);
        Assert.Equal(parsed.Frames!.Value - 12, retake.LengthFrames);
        Assert.Equal(27, parsed.Frames);
    }

    [Fact]
    public void ParseClips_Retake_DefaultsStrengthToOneWhenAbsent()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 1.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildInitVideoParser(json)).Clips);

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
        clip["retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 1.0, strength: 5.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildInitVideoParser(json)).Clips);

        Assert.Equal(1.0, parsed.Stages[^1].RetakeWindow.Strength, 6);
    }

    [Fact]
    public void ParseClips_Retake_NullWhenClipHasNoInitVideo()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["retake"] = MakeRetake(startSeconds: 1.0, lengthSeconds: 2.0);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildParser(json)).Clips);

        Assert.All(parsed.Stages, stage => Assert.Null(stage.RetakeWindow));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, -1.0)]
    [InlineData(-1.0, 2.0)]
    public void ParseClips_Retake_NullWhenInvalidWindow(double startSeconds, double lengthSeconds)
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        clip["retake"] = MakeRetake(startSeconds, lengthSeconds);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildInitVideoParser(json)).Clips);

        Assert.All(parsed.Stages, stage => Assert.Null(stage.RetakeWindow));
    }

    [Fact]
    public void ParseClips_Retake_NullWhenSubFrameLengthRoundsToZero()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        // 0.01s at 24 fps => round(0.24) = 0 frames => disabled.
        clip["retake"] = MakeRetake(startSeconds: 0.0, lengthSeconds: 0.01);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildInitVideoParser(json)).Clips);

        Assert.Null(parsed.Stages[^1].RetakeWindow);
    }

    [Fact]
    public void ParseClips_Retake_AbsentLeavesStagesUntouched()
    {
        JObject clip = MakeClip(stages: [MakeStage("model-a")]);
        string json = JsonConvert.SerializeObject(new JArray(clip));

        ClipSpec parsed = Assert.Single(VideoStagesSpecParser.Parse(BuildInitVideoParser(json)).Clips);

        Assert.Null(parsed.Stages[^1].RetakeWindow);
    }

    [Fact]
    public void Parse_RootTimelineAudioSegments_PreservesExecutableSourceWindowAndVolume()
    {
        JObject span = AudioSpan(
            timelineStartSeconds: 1.5,
            timelineLengthSeconds: 2.5,
            sourceStartSeconds: 4);
        span["projection"] = new JObject
        {
            ["firstClipId"] = "clip-a",
            ["lastClipId"] = "clip-a",
            ["clipStartOffsetSeconds"] = 1.5,
            ["clipEndOffsetSeconds"] = 4,
        };
        JObject root = new()
        {
            ["clips"] = new JArray(MakeClip([MakeStage("ltx-2")], duration: 4)),
            ["audioTracks"] = new JArray(
                AudioTrack("track-dialogue", volume: 0.75, fileName: "dialogue.wav", span)),
        };
        ((JObject)((JArray)root["clips"])[0])["id"] = "clip-a";

        VideoStagesSpec parsed = VideoStagesSpecParser.Parse(
            BuildParser(root.ToString(Formatting.None)));
        TimelineAudioSegmentSpec segment = Assert.Single(parsed.TimelineAudioSegments);

        Assert.Equal("track-dialogue", segment.Id);
        Assert.Equal("dialogue.wav", segment.Source.FileName);
        Assert.Equal(1.5, segment.TimelineStartSeconds);
        Assert.Equal(2.5, segment.LengthSeconds);
        Assert.Equal(4, segment.SourceStartSeconds);
        Assert.Equal(0.75, segment.Volume);
        Assert.Equal(0, segment.FirstClipId);
        Assert.Equal(0, segment.LastClipId);
        Assert.Equal(1.5, segment.FirstClipOffsetSeconds);
        Assert.Equal(4, segment.LastClipOffsetSeconds);
    }

    private static JObject MakeAudioTrack(string id, params JObject[] spans) =>
        AudioTrack(id, volume: 0.5, fileName: "score.wav", spans);

    [Fact]
    public void Parse_RootTimelineAudioSegments_MultiSpanTrackMatchesSplitSingleSpanLanes()
    {
        static VideoStagesSpec ParseWithTracks(params JObject[] tracks)
        {
            JObject root = new()
            {
                ["clips"] = new JArray(MakeClip([MakeStage("ltx-2")], duration: 8)),
                ["audioTracks"] = new JArray(tracks.Cast<object>().ToArray()),
            };
            ((JObject)((JArray)root["clips"])[0])["id"] = "clip-a";
            return VideoStagesSpecParser.Parse(BuildParser(root.ToString(Formatting.None)));
        }

        IReadOnlyList<TimelineAudioSegmentSpec> combined = ParseWithTracks(
            MakeAudioTrack(
                "track-multi",
                AudioSpan(0, 1, 0),
                AudioSpan(3, 2, 5))).TimelineAudioSegments;
        IReadOnlyList<TimelineAudioSegmentSpec> split = ParseWithTracks(
            MakeAudioTrack("track-multi:0", AudioSpan(0, 1, 0)),
            MakeAudioTrack("track-multi:1", AudioSpan(3, 2, 5))).TimelineAudioSegments;

        Assert.Equal(2, combined.Count);
        Assert.Equal(
            ["track-multi:0", "track-multi:1"],
            combined.Select(segment => segment.Id).ToArray());
        Assert.Equal(
            combined.Select(segment => (
                segment.Id,
                segment.TimelineStartSeconds,
                segment.LengthSeconds,
                segment.SourceStartSeconds,
                segment.Volume)).ToArray(),
            split.Select(segment => (
                segment.Id,
                segment.TimelineStartSeconds,
                segment.LengthSeconds,
                segment.SourceStartSeconds,
                segment.Volume)).ToArray());
    }
}
