using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AceStepFunAudioSavePrunerTests
{
    private static JsonParser.ClipSpec Clip(int id, string audioSource, bool saveAudioTrack) => new(
        Id: id,
        Skipped: false,
        DurationSeconds: 3,
        AudioSource: audioSource,
        ControlNetSource: Constants.ControlNetSourceOne,
        ControlNetLora: "",
        SaveAudioTrack: saveAudioTrack,
        ClipLengthFromAudio: false,
        ClipLengthFromControlNet: false,
        ReuseAudio: false,
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
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), "64160");

            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save1.Audio.ConnectTo(decode.AUDIO);
            bridge.AddNode(save1, "64170");

            SaveAudioMP3Node save2 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_2_");
            bridge.AddNode(save2, "64270");
        }

        AceStepFunAudioSavePruner.Apply(
            CreateGenerator(workflow),
            [Clip(id: 0, audioSource: "audio0", saveAudioTrack: false)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.Null(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64270"));
        Assert.NotNull(after.Graph.GetNode<VAEDecodeAudioNode>("64160"));
    }

    [Fact]
    public void Apply_KeepsAceStepFunSaveNode_WhenSelectedTrackSavesAudio()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), "64160");

            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save1.Audio.ConnectTo(decode.AUDIO);
            bridge.AddNode(save1, "64170");
        }

        AceStepFunAudioSavePruner.Apply(
            CreateGenerator(workflow),
            [Clip(id: 0, audioSource: "audio0", saveAudioTrack: true)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
    }

    [Fact]
    public void Apply_KeepsOnlySelectedAceStepFunTracksMarkedForSaving()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            bridge.AddNode(save1, "64170");

            SaveAudioMP3Node save2 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_2_");
            bridge.AddNode(save2, "64270");
        }

        AceStepFunAudioSavePruner.Apply(
            CreateGenerator(workflow),
            [
                Clip(id: 0, audioSource: "audio0", saveAudioTrack: false),
                Clip(id: 1, audioSource: "audio1", saveAudioTrack: true)
            ]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.Null(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64270"));
    }

    [Fact]
    public void Apply_KeepsAceStepFunSaveNodes_WhenNoAceStepFunTrackIsSelected()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            bridge.AddNode(save1, "64170");
        }

        AceStepFunAudioSavePruner.Apply(
            CreateGenerator(workflow),
            [Clip(id: 0, audioSource: "Native", saveAudioTrack: false)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
    }
}
