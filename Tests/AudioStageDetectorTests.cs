using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class AudioStageDetectorTests
{
    private const string VaeDecodeAudio = "VAEDecodeAudio";
    private const string SwarmSaveAudioWs = "SwarmSaveAudioWS";

    private static JObject Node(string classType, JObject inputs = null) => new()
    {
        ["class_type"] = classType,
        ["inputs"] = inputs ?? new JObject()
    };

    private static WorkflowGenerator CreateGenerator(JObject workflow)
    {
        WorkflowGenerator generator = new()
        {
            UserInput = new T2IParamInput(null),
            Features = [],
            ModelFolderFormat = "/",
            Workflow = workflow
        };
        generator.CurrentAudioVae = new WGNodeData(new JArray("900", 0), generator, WGNodeData.DT_AUDIOVAE, T2IModelClassSorter.CompatLtxv2);
        return generator;
    }

    [Fact]
    public void Detector_prefers_swarm_save_audio_ws_over_other_audio_stage_nodes()
    {
        JObject workflow = new()
        {
            ["100"] = Node(VaeDecodeAudio),
            ["110"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("100", 0)
            }),
            ["120"] = Node(SwarmSaveAudioWs, new JObject()
            {
                ["audio"] = new JArray("100", 0)
            })
        };

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).Detect();

        Assert.NotNull(detection);
        Assert.Equal("120", detection.MatchedNodeId);
        Assert.Equal(SwarmSaveAudioWs, detection.MatchedClassType);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("100", 0)));
    }

    [Fact]
    public void Detector_prefers_latest_save_audio_node_when_multiple_are_present()
    {
        JObject workflow = new()
        {
            ["100"] = Node(VaeDecodeAudio),
            ["110"] = Node("SaveAudioMP3", new JObject()
            {
                ["audio"] = new JArray("100", 0)
            }),
            ["210"] = Node("SaveAudioWAV", new JObject()
            {
                ["audio"] = new JArray("100", 0)
            })
        };

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).Detect();

        Assert.NotNull(detection);
        Assert.Equal("210", detection.MatchedNodeId);
        Assert.Equal("SaveAudioWAV", detection.MatchedClassType);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("100", 0)));
    }

    [Fact]
    public void Detector_falls_back_to_latest_vae_decode_audio_when_no_save_nodes_exist()
    {
        JObject workflow = new()
        {
            ["100"] = Node(VaeDecodeAudio),
            ["150"] = Node(VaeDecodeAudio)
        };

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).Detect();

        Assert.NotNull(detection);
        Assert.Equal("150", detection.MatchedNodeId);
        Assert.Equal(VaeDecodeAudio, detection.MatchedClassType);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("150", 0)));
    }

    [Fact]
    public void DetectAceStepFunTrack_UsesMatchingTrackSaveNode()
    {
        JObject workflow = new()
        {
            ["64160"] = Node(VaeDecodeAudio),
            ["64260"] = Node(VaeDecodeAudio),
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

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).DetectAceStepFunTrack("audio0");

        Assert.NotNull(detection);
        Assert.Equal("64170", detection.MatchedNodeId);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("64160", 0)));
    }

    [Fact]
    public void DetectAceStepFunTrack_FallsBackToStableDecodeNode()
    {
        JObject workflow = new()
        {
            ["64160"] = Node(VaeDecodeAudio),
            ["64260"] = Node(VaeDecodeAudio)
        };

        AudioStageDetector.Detection detection = new AudioStageDetector(CreateGenerator(workflow)).DetectAceStepFunTrack("audio1");

        Assert.NotNull(detection);
        Assert.Equal("64260", detection.MatchedNodeId);
        Assert.True(JToken.DeepEquals(detection.Audio.Path, new JArray("64260", 0)));
    }
}
