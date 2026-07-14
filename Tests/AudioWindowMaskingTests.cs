using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using VideoStages.LTX2;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Audio_retake_window_seconds_are_deterministic()
    {
        LtxAudioWindowMasker.AudioMaskWindow w =
            LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(24, 24, 1.0), fps: 24, clipFrames: 97);
        Assert.Equal(1.0, w.StartTime, 6);
        Assert.Equal(2.0, w.EndTime, 6);
        Assert.False(w.IsEmpty);

        // Over-long window is clamped to the clip length.
        LtxAudioWindowMasker.AudioMaskWindow clamped =
            LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(48, 999, 1.0), fps: 24, clipFrames: 96);
        Assert.Equal(2.0, clamped.StartTime, 6);
        Assert.Equal(4.0, clamped.EndTime, 6);

        // Zero-length retake => empty (no windowing).
        Assert.True(LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(0, 0, 1.0), 24, 97).IsEmpty);
    }

    [Fact]
    public void Retake_with_audio_windows_the_audio_latent_to_the_retake_range()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStagesWithRetake(
            startSeconds: 1.0, lengthSeconds: 1.0, strength: 0.8,
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);
        EnableRefineMode(input);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Video retake mask is still applied to the video latent (unchanged path).
        Assert.Single(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());

        // Audio channel is windowed by time via the stock mask-by-time node, audio-only.
        LTXVSetAudioVideoMaskByTimeNode maskByTime = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());
        JObject inputs = (JObject)AsWorkflowNode(maskByTime, workflow).Node["inputs"];
        Assert.False(inputs["mask_video"]!.Value<bool>());
        Assert.True(inputs["mask_audio"]!.Value<bool>());
        // Video init MUST be 1.0 (identity): the node multiplies the retake video mask into this, so 0.0
        // would zero it and freeze the entire video.
        Assert.Equal(1.0, inputs["mask_init_value_video"]!.Value<double>(), 6);
        Assert.Equal(0.0, inputs["mask_init_value_audio"]!.Value<double>(), 6);
        Assert.Equal(1.0, maskByTime.StartTime.LiteralAsDouble()!.Value, 6);
        Assert.Equal(2.0, maskByTime.EndTime.LiteralAsDouble()!.Value, 6);

        // Its rewritten av-latent + conditioning feed the sampler.
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.True(ReachesUpstream(bridge, sampler.LatentImage.Connection!.Node, maskByTime.Id));
        Assert.True(ReachesUpstream(bridge, sampler.Positive.Connection!.Node, maskByTime.Id));
    }

    [Fact]
    public void Plain_audio_clip_without_retake_adds_no_mask_by_time_node()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", control: 1.0, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());
    }
}
