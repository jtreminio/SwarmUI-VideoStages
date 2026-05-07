using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioStageDetectorTests
{
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
    public void Detector_prefers_latest_save_audio_node_when_multiple_are_present()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAEDecodeAudioNode decode = bridge.AddNode(new VAEDecodeAudioNode(), "100");

        SaveAudioMP3Node saveMp3 = new();
        saveMp3.Audio.ConnectTo(decode.AUDIO);
        bridge.AddNode(saveMp3, "110");

        SaveAudioNode save = new();
        save.Audio.ConnectTo(decode.AUDIO);
        bridge.AddNode(save, "210");

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).Detect();

        Assert.NotNull(detection);
        Assert.Equal("210", detection.MatchedNodeId);
        Assert.Equal("SaveAudio", detection.MatchedClassType);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("100", 0)));
    }

    [Fact]
    public void Detector_falls_back_to_latest_vae_decode_audio_when_no_save_nodes_exist()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.AddNode(new VAEDecodeAudioNode(), "100");
        bridge.AddNode(new VAEDecodeAudioNode(), "150");

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).Detect();

        Assert.NotNull(detection);
        Assert.Equal("150", detection.MatchedNodeId);
        Assert.Equal(VAEDecodeAudioNode.ClassType, detection.MatchedClassType);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("150", 0)));
    }

    [Fact]
    public void Detector_ignores_acestepfun_nodes_for_native_audio_detection()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.AddNode(new VAEDecodeAudioNode(), "100");
        VAEDecodeAudioNode aceDecode = bridge.AddNode(new VAEDecodeAudioNode(), "64160");

        SaveAudioMP3Node aceSave = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
        aceSave.Audio.ConnectTo(aceDecode.AUDIO);
        bridge.AddNode(aceSave, "64170");

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).Detect();

        Assert.NotNull(detection);
        Assert.Equal("100", detection.MatchedNodeId);
        Assert.Equal(VAEDecodeAudioNode.ClassType, detection.MatchedClassType);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("100", 0)));
    }

    [Fact]
    public void DetectAceStepFunTrack_UsesMatchingTrackSaveNode()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        VAEDecodeAudioNode decode1 = bridge.AddNode(new VAEDecodeAudioNode(), "64160");
        VAEDecodeAudioNode decode2 = bridge.AddNode(new VAEDecodeAudioNode(), "64260");

        SaveAudioMP3Node save1 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_1_");
        save1.Audio.ConnectTo(decode1.AUDIO);
        bridge.AddNode(save1, "64170");

        SaveAudioMP3Node save2 = new SaveAudioMP3Node().With(FilenamePrefix: "SwarmUI_track_2_");
        save2.Audio.ConnectTo(decode2.AUDIO);
        bridge.AddNode(save2, "64270");

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow))
            .DetectAceStepFunTrack("audio0");

        Assert.NotNull(detection);
        Assert.Equal("64170", detection.MatchedNodeId);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("64160", 0)));
    }

    [Fact]
    public void DetectAceStepFunTrack_FallsBackToStableDecodeNode()
    {
        JObject workflow = [];
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        bridge.AddNode(new VAEDecodeAudioNode(), "64160");
        bridge.AddNode(new VAEDecodeAudioNode(), "64260");

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow))
            .DetectAceStepFunTrack("audio1");

        Assert.NotNull(detection);
        Assert.Equal("64260", detection.MatchedNodeId);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("64260", 0)));
    }
}
