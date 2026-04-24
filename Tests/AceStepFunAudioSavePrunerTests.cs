using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AceStepFunAudioSavePrunerTests
{
    private static JObject Node(string classType, JObject inputs = null) => new()
    {
        ["class_type"] = classType,
        ["inputs"] = inputs ?? new JObject()
    };

    private static JsonParser.ClipSpec Clip(int id, string audioSource, bool saveAudioTrack) => new(
        Id: id,
        Skipped: false,
        DurationSeconds: 3,
        AudioSource: audioSource,
        SaveAudioTrack: saveAudioTrack,
        ClipLengthFromAudio: false,
        Width: null,
        Height: null,
        UploadedAudio: null,
        Refs: [],
        Stages: []
    );

    private static WorkflowGenerator CreateGenerator(JObject workflow) => new()
    {
        UserInput = new T2IParamInput(null),
        Workflow = workflow
    };

    [Fact]
    public void Apply_RemovesAceStepFunSaveNode_WhenSelectedTrackDoesNotSaveAudio()
    {
        JObject workflow = new()
        {
            ["64160"] = Node("VAEDecodeAudio"),
            ["64170"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("64160", 0),
                ["filename_prefix"] = "SwarmUI_track_1_"
            }),
            ["64270"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("64260", 0),
                ["filename_prefix"] = "SwarmUI_track_2_"
            })
        };

        AceStepFunAudioSavePruner.Apply(CreateGenerator(workflow), [Clip(0, "audio0", saveAudioTrack: false)]);

        Assert.False(workflow.ContainsKey("64170"));
        Assert.True(workflow.ContainsKey("64270"));
        Assert.True(workflow.ContainsKey("64160"));
    }

    [Fact]
    public void Apply_KeepsAceStepFunSaveNode_WhenSelectedTrackSavesAudio()
    {
        JObject workflow = new()
        {
            ["64170"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("64160", 0),
                ["filename_prefix"] = "SwarmUI_track_1_"
            })
        };

        AceStepFunAudioSavePruner.Apply(CreateGenerator(workflow), [Clip(0, "audio0", saveAudioTrack: true)]);

        Assert.True(workflow.ContainsKey("64170"));
    }

    [Fact]
    public void Apply_KeepsOnlySelectedAceStepFunTracksMarkedForSaving()
    {
        JObject workflow = new()
        {
            ["64170"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("64160", 0),
                ["filename_prefix"] = "SwarmUI_track_1_"
            }),
            ["64270"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("64260", 0),
                ["filename_prefix"] = "SwarmUI_track_2_"
            })
        };

        AceStepFunAudioSavePruner.Apply(
            CreateGenerator(workflow),
            [
                Clip(0, "audio0", saveAudioTrack: false),
                Clip(1, "audio1", saveAudioTrack: true)
            ]);

        Assert.False(workflow.ContainsKey("64170"));
        Assert.True(workflow.ContainsKey("64270"));
    }

    [Fact]
    public void Apply_KeepsAceStepFunSaveNodes_WhenNoAceStepFunTrackIsSelected()
    {
        JObject workflow = new()
        {
            ["64170"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("64160", 0),
                ["filename_prefix"] = "SwarmUI_track_1_"
            })
        };

        AceStepFunAudioSavePruner.Apply(CreateGenerator(workflow), [Clip(0, "Native", saveAudioTrack: false)]);

        Assert.True(workflow.ContainsKey("64170"));
    }
}
