using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    private static readonly string[] RetakeFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    // Retake lives in the per-clip JSON (seconds-based); a clip source video is the enable gate.
    private static JObject RetakeInitVideo() => new()
    {
        ["data"] = "data:video/mp4;base64," + Convert.ToBase64String([0xDE, 0xAD, 0xBE, 0xEF]),
        ["fileName"] = "refine.mp4",
        ["startSeconds"] = 0.0
    };

    internal static string JsonSingleClipStagesWithRetake(
        double startSeconds,
        double lengthSeconds,
        double? strength,
        params JObject[] stages) =>
        JsonSingleClipStagesWithRetake(startSeconds, lengthSeconds, strength, true, stages);

    internal static string JsonSingleClipStagesWithRetake(
        double startSeconds,
        double lengthSeconds,
        double? strength,
        bool initVideoClip,
        params JObject[] stages)
    {
        JObject clip = MakeClip(stages);
        JObject retake = new()
        {
            ["startSeconds"] = startSeconds,
            ["lengthSeconds"] = lengthSeconds
        };
        if (strength is not null)
        {
            retake["strength"] = strength.Value;
        }
        clip["retake"] = retake;
        if (initVideoClip)
        {
            // A source video is only honoured on a clip with a usable duration; 4.0s @ 24fps aligns
            // up to the 97 frames these tests request through VideoFrames.
            ((JObject)((JArray)clip["stages"])[0]).Remove("imageReference");
            clip["duration"] = 4.0;
            clip["initVideo"] = RetakeInitVideo();
        }
        return new JArray(clip).ToString();
    }

    [Fact]
    public void Retake_latent_window_arithmetic_is_deterministic()
    {
        LtxVideoRetakeMasker.LatentWindow w =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames: 97, startFrame: 24, lengthFrames: 24);
        Assert.Equal(3, w.Prefix);
        Assert.Equal(3, w.Window);
        Assert.Equal(7, w.Suffix);
        Assert.Equal(13, w.LatentLength);

        LtxVideoRetakeMasker.LatentWindow full =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames: 16, startFrame: 0, lengthFrames: 16);
        Assert.Equal(0, full.Prefix);
        Assert.Equal(2, full.Window);
        Assert.Equal(0, full.Suffix);

        // Over-long request is clamped to the clip.
        LtxVideoRetakeMasker.LatentWindow clamped =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames: 25, startFrame: 8, lengthFrames: 999);
        Assert.Equal(1, clamped.Prefix);
        Assert.Equal(clamped.LatentLength - 1, clamped.Window);
        Assert.Equal(0, clamped.Suffix);
    }

    [Fact]
    public void Retake_disabled_when_length_zero_leaves_graph_unchanged()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Refine source present, Retake with zero length => retake off.
        string stagesJson = JsonSingleClipStagesWithRetake(
            startSeconds: 1.0, lengthSeconds: 0.0, strength: 1.0,
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        // StartStep still follows the stage control: floor(10 x (1 - 0.5)). A leaked retake would
        // force it to 0, which is why the stage cannot be authored at control 1.0 here.
        Assert.Equal(5, sampler.StartAtStep.LiteralAsInt());
    }

    [Fact]
    public void Retake_ignored_when_clip_is_not_init_video()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Retake window present in the clip JSON but no clip source video => retake never activates.
        string stagesJson = JsonSingleClipStagesWithRetake(
            startSeconds: 1.0, lengthSeconds: 1.0, strength: 0.8, initVideoClip: false,
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        // Positive control: without it, a graph that stopped building the stage at all would pass.
        // StartAtStep cannot serve — the parser forces stage 0 of a non-sourced clip to control
        // 1.0, so it is 0 here for the same reason an active retake would force it to 0.
        SwarmKSamplerNode sampler = Assert.Single(bridge.Graph.NodesOfType<SwarmKSamplerNode>());
        Assert.Equal(10, sampler.Steps.LiteralAsInt());
    }
}
