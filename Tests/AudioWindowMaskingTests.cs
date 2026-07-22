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
        // Frames [24, 48) → latent frames [3, 6): the seconds are snapped to the latent boundary
        // pixels (17 and 41) so the mask-by-time node selects exactly those latent frames.
        LtxAudioWindowMasker.AudioMaskWindow w =
            LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(24, 24, 1.0), fps: 24, clipFrames: 97);
        Assert.Equal(17.0 / 24, w.StartTime, 6);
        Assert.Equal(41.0 / 24, w.EndTime, 6);
        Assert.False(w.IsEmpty);

        // Over-long window is clamped to the clip length (latents [6, 12) of 12 → pixels 41–89).
        LtxAudioWindowMasker.AudioMaskWindow clamped =
            LtxAudioWindowMasker.ComputeRetakeWindow(new RetakeWindowSpec(48, 999, 1.0), fps: 24, clipFrames: 96);
        Assert.Equal(41.0 / 24, clamped.StartTime, 6);
        Assert.Equal(89.0 / 24, clamped.EndTime, 6);

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

        // Both channels are windowed by time via the stock mask-by-time node. The node must own the
        // video mask (its merge of a pre-existing mask only fires for per-frame scalar masks, and the
        // retake mask is H×W-resized): mask_video=true over a frozen 0.0 base, matching windows.
        LTXVSetAudioVideoMaskByTimeNode maskByTime = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());
        JObject inputs = (JObject)AsWorkflowNode(maskByTime, workflow).Node["inputs"];
        Assert.True(inputs["mask_video"]!.Value<bool>());
        Assert.True(inputs["mask_audio"]!.Value<bool>());
        Assert.Equal(0.0, inputs["mask_init_value_video"]!.Value<double>(), 6);
        Assert.Equal(0.0, inputs["mask_init_value_audio"]!.Value<double>(), 6);
        // Latent-snapped seconds: frames [24, 48) → latents [3, 6) → boundary pixels 17 and 41.
        Assert.Equal(17.0 / 24, maskByTime.StartTime.LiteralAsDouble()!.Value, 6);
        Assert.Equal(41.0 / 24, maskByTime.EndTime.LiteralAsDouble()!.Value, 6);

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
