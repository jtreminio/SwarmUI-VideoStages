using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
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
        string name,
        IEnumerable<JObject> stages,
        IEnumerable<JObject> refs = null,
        bool skipped = false,
        double duration = 3.0,
        int width = 1024,
        int height = 768)
    {
        return new JObject
        {
            ["Name"] = name,
            ["Skipped"] = skipped,
            ["Duration"] = duration,
            ["Width"] = width,
            ["Height"] = height,
            ["Refs"] = new JArray(refs ?? []),
            ["Stages"] = new JArray(stages),
        };
    }

    private static T2IParamInput BuildInputWithJson(string json)
    {
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        T2IParamInput input = new(null);
        input.Set(VideoStagesExtension.EnableVideoStages, true);
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
    public void ParseClips_LegacyStageArray_TreatedAsSingleClip()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeStage("model-a"),
            MakeStage("model-b")
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.ClipSpec> clips = parser.ParseClips();

        Assert.Single(clips);
        Assert.Equal("Clip 0", clips[0].Name);
        Assert.False(clips[0].Skipped);
        Assert.Empty(clips[0].Refs);
        Assert.Equal(2, clips[0].Stages.Count);
        Assert.Equal("model-a", clips[0].Stages[0].Model);
        Assert.Equal("model-b", clips[0].Stages[1].Model);
    }

    [Fact]
    public void ParseClips_ClipShape_PopulatesPerClipFields()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip("First Clip",
                stages: [MakeStage("model-a")],
                refs: [MakeRef("Base", frame: 1), MakeRef("Refiner", frame: 12, fromEnd: true)],
                duration: 4.0,
                width: 800,
                height: 600),
            MakeClip("Second Clip",
                stages: [MakeStage("model-b"), MakeStage("model-c")],
                duration: 6.0)
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.ClipSpec> clips = parser.ParseClips();

        Assert.Equal(2, clips.Count);

        Assert.Equal("First Clip", clips[0].Name);
        Assert.Equal(4.0, clips[0].DurationSeconds);
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

        Assert.Equal("Second Clip", clips[1].Name);
        Assert.Equal(6.0, clips[1].DurationSeconds);
        Assert.Empty(clips[1].Refs);
        Assert.Equal(2, clips[1].Stages.Count);
    }

    [Fact]
    public void ParseStages_Flattens_ClipShape_AcrossClips_AssigningSequentialIds()
    {
        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip("First", stages: [MakeStage("model-a"), MakeStage("model-b")]),
            MakeClip("Second", stages: [MakeStage("model-c")])
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
            MakeClip("First", stages: [MakeStage("model-a"), skippedStage]),
            MakeClip("Second", stages: [MakeStage("model-skipped-clip")], skipped: true),
            MakeClip("Third", stages: [MakeStage("model-c")])
        ));
        JsonParser parser = BuildParser(json);

        List<JsonParser.StageSpec> stages = parser.ParseStages();

        Assert.Equal(2, stages.Count);
        Assert.Equal("model-a", stages[0].Model);
        Assert.Equal("model-c", stages[1].Model);
    }

    [Fact]
    public void ParseClips_StagesMissingModel_AreSkippedSilently()
    {
        JObject brokenStage = MakeStage("");
        brokenStage["Model"] = "";

        string json = JsonConvert.SerializeObject(new JArray(
            MakeClip("First", stages: [brokenStage, MakeStage("model-a")])
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
            MakeClip("First",
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
            MakeClip("First",
                stages: [MakeStage("model-a")],
                duration: 4.0,
                width: 800,
                height: 600),
            MakeClip("Second",
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
        Assert.Equal(96, stages[0].ClipFrames);
        Assert.Equal(1920, stages[1].ClipWidth);
        Assert.Equal(1080, stages[1].ClipHeight);
        Assert.Equal(48, stages[1].ClipFrames);
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
            MakeClip("First",
                stages: [MakeStage("model-a")],
                refs: [uploadRef])
        ));
        JsonParser parser = BuildParser(json);

        JsonParser.ClipSpec clip = parser.ParseClips().Single();
        Assert.Equal("Upload", clip.Refs[0].Source);
        Assert.Equal("ref.png", clip.Refs[0].UploadFileName);
    }
}
