using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioHandlerTests
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

    private static WorkflowGenerator CreateGenerator(JObject workflow)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow
        };
        generator.CurrentAudioVae = new WGNodeData(
            new JArray("900", 0), generator, WGNodeData.DT_AUDIOVAE, T2IModelClassSorter.CompatLtxv2);
        return generator;
    }

    [Fact]
    public void DetectAceStepFunAudio_returns_decode_audio_for_matching_track()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));
        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(1));

        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio("audio1");

        Assert.True(JToken.DeepEquals(
            audio.Path,
            new JArray(AudioHandler.MakeAceStepFunDecodeId(1), 0)));
    }

    [Fact]
    public void DetectAceStepFunAudio_finds_decode_when_downstream_save_is_absent()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio("audio0");

        Assert.True(JToken.DeepEquals(
            audio.Path,
            new JArray(AudioHandler.MakeAceStepFunDecodeId(0), 0)));
    }

    [Fact]
    public void DetectAceStepFunAudio_returns_null_for_non_acestepfun_source()
    {
        JObject workflow = [];
        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio(Constants.AudioSourceNative);

        Assert.Null(audio);
    }

    [Fact]
    public void DetectAceStepFunAudio_returns_null_when_no_decode_matches_track()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

        WGNodeData audio = new AudioHandler(CreateGenerator(workflow))
            .DetectAceStepFunAudio("audio7");

        Assert.Null(audio);
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_removes_downstream_saves_of_any_audio_save_type()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

            SaveAudioMP3Node mp3 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            mp3.Audio.ConnectTo(decode.AUDIO);
            bridge.AddNode(mp3, "64170");

            SaveAudioNode wav = new();
            wav.Audio.ConnectTo(decode.AUDIO);
            bridge.AddNode(wav, "64171");

            SaveAudioMP3Node unrelated = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_2_");
            bridge.AddNode(unrelated, "64270");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [Clip(id: 0, audioSource: "audio0", saveAudioTrack: false)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.Null(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
        Assert.Null(after.Graph.GetNode<SaveAudioNode>("64171"));
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64270"));
        Assert.NotNull(after.Graph.GetNode<VAEDecodeAudioNode>(AudioHandler.MakeAceStepFunDecodeId(0)));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_keeps_save_when_track_marked_to_save()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));

            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save1.Audio.ConnectTo(decode.AUDIO);
            bridge.AddNode(save1, "64170");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [Clip(id: 0, audioSource: "audio0", saveAudioTrack: true)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_keeps_save_when_any_clip_wants_track_saved()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode0 = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));
            VAEDecodeAudioNode decode1 = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(1));

            SaveAudioMP3Node save0 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save0.Audio.ConnectTo(decode0.AUDIO);
            bridge.AddNode(save0, "64170");

            SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_2_");
            save1.Audio.ConnectTo(decode1.AUDIO);
            bridge.AddNode(save1, "64270");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [
                Clip(id: 0, audioSource: "audio0", saveAudioTrack: false),
                Clip(id: 1, audioSource: "audio1", saveAudioTrack: true)
            ]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.Null(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64270"));
    }

    [Fact]
    public void PruneAceStepFunUnsavedTracks_no_op_when_no_acestepfun_track_referenced()
    {
        JObject workflow = [];
        using (WorkflowBridge bridge = WorkflowBridge.Create(workflow))
        {
            VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(0));
            SaveAudioMP3Node save = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
            save.Audio.ConnectTo(decode.AUDIO);
            bridge.AddNode(save, "64170");
        }

        new AudioHandler(CreateGenerator(workflow)).PruneAceStepFunUnsavedTracks(
            [Clip(id: 0, audioSource: Constants.AudioSourceNative, saveAudioTrack: false)]);

        using WorkflowBridge after = WorkflowBridge.Create(workflow);
        Assert.NotNull(after.Graph.GetNode<SaveAudioMP3Node>("64170"));
    }

}
