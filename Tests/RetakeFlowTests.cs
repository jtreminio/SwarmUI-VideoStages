using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    private static readonly string[] RetakeFeatures =
        [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"];

    // Retake now lives in the per-clip JSON (seconds-based); a clip source video is the enable gate.
    // At fps 24, start/length of 1.0s each resolve to frame windows [24, 24), matching the old
    // param-based tests.
    internal static JObject RetakeSourceVideo() => new()
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
        bool sourced,
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
        if (sourced)
        {
            // A source video is only honoured on a clip with a usable duration; 4.0s @ 24fps aligns
            // up to the 97 frames these tests request through VideoFrames.
            ((JObject)((JArray)clip["stages"])[0]).Remove("imageReference");
            clip["duration"] = 4.0;
            clip["sourceVideo"] = RetakeSourceVideo();
        }
        return new JArray(clip).ToString();
    }

    [Fact]
    public void Retake_window_attaches_noise_mask_and_forces_full_start_step()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStagesWithRetake(
            startSeconds: 1.0, lengthSeconds: 1.0, strength: 0.8,
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        // Sampler runs from step 0 so the per-frame mask, not StartStep, gates what regenerates.
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Equal(0, sampler.StartAtStep.LiteralAsInt());
        // No inplace guide merges on a retake stage: LTXVImgToVideoInplace overwrites the noise mask of
        // every frame it conditions and would clobber the retake window.
        Assert.Empty(bridge.Graph.NodesOfType<LTXVImgToVideoInplaceNode>());
        Assert.True(
            ReachesUpstream(bridge, sampler.LatentImage.Connection!.Node, maskNode.Id),
            "Sampler latent input does not trace upstream to the retake noise-mask node.");
        Assert.True(
            ReachesUpstream(bridge, maskNode.Samples.Connection!.Node, loadVideo.Id),
            "Retake noise-mask samples input does not trace upstream to the base (loaded) video.");
    }

    [Fact]
    public void Retake_mask_block_lengths_match_window()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        const int pixelFrames = 97;
        const int start = 24;
        const int length = 24;
        const double strength = 0.75;

        string stagesJson = JsonSingleClipStagesWithRetake(
            startSeconds: 1.0, lengthSeconds: 1.0, strength: strength,
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, pixelFrames);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LtxVideoRetakeMasker.LatentWindow expected =
            LtxVideoRetakeMasker.ComputeLatentWindow(pixelFrames, start, length);

        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());

        List<RepeatImageBatchNode> repeats = bridge.Graph.NodesOfType<RepeatImageBatchNode>()
            .Where(r => ReachesUpstream(bridge, maskNode, r.Id))
            .OrderBy(r => int.Parse(r.Id))
            .ToList();

        int[] amounts = [.. repeats.Select(r => r.Amount.LiteralAsInt() ?? 0)];
        Assert.Equal(new[] { expected.Prefix, expected.Window, expected.Suffix }, amounts);
        Assert.Equal(expected.LatentLength, amounts.Sum());

        // Exactly the window block carries the retake strength; the frozen blocks are value 0.
        List<SolidMaskNode> solids = bridge.Graph.NodesOfType<SolidMaskNode>()
            .Where(s => ReachesUpstream(bridge, maskNode, s.Id))
            .ToList();
        Assert.Single(solids, s => Math.Abs((s.Value.LiteralAsDouble() ?? -1) - strength) < 1e-6);
        Assert.Equal(2, solids.Count(s => Math.Abs(s.Value.LiteralAsDouble() ?? -1) < 1e-6));
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
    public void Retake_reaching_clip_end_covers_the_aligned_tail()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Duration 4.0s @ 24fps aligns UP to 97 frames (4.042s). A retake ending exactly at the
        // authored 4.0s must still regenerate through the aligned tail — no frozen suffix block.
        JObject clip = MakeClip(MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));
        clip["duration"] = 4.0;
        clip["retake"] = new JObject
        {
            ["startSeconds"] = 2.0,
            ["lengthSeconds"] = 2.0,
            ["strength"] = 0.9
        };
        clip["sourceVideo"] = RetakeSourceVideo();
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: true),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        LTXVSetVideoLatentNoiseMasksNode maskNode = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        int[] amounts = [.. bridge.Graph.NodesOfType<RepeatImageBatchNode>()
            .Where(r => ReachesUpstream(bridge, maskNode, r.Id))
            .OrderBy(r => int.Parse(r.Id))
            .Select(r => r.Amount.LiteralAsInt() ?? 0)];
        // Latents [6, 13) of 13 regenerate: prefix 6, window 7, and NO frozen suffix block.
        Assert.Equal(new[] { 6, 7 }, amounts);

        // The av window's end lands past the aligned video end (latent 13 boundary: pixel 97).
        LTXVSetAudioVideoMaskByTimeNode maskByTime = Assert.Single(
            bridge.Graph.NodesOfType<LTXVSetAudioVideoMaskByTimeNode>());
        Assert.Equal(41.0 / 24, maskByTime.StartTime.LiteralAsDouble()!.Value, 6);
        Assert.Equal(97.0 / 24, maskByTime.EndTime.LiteralAsDouble()!.Value, 6);
    }

    [Fact]
    public void Retake_disabled_when_length_zero_leaves_graph_unchanged()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Refine source present, Retake with zero length => retake off.
        string stagesJson = JsonSingleClipStagesWithRetake(
            startSeconds: 1.0, lengthSeconds: 0.0, strength: 1.0,
            MakeStage(models.VideoModel.Name, "Generated", control: 1.0, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
        SwarmKSamplerNode sampler = Assert.Single(SamplerNodesOrdered(bridge));
        // Back-compat: StartStep follows the stage control (index-0 stage forced to control 1.0 => 0).
        Assert.Equal(0, sampler.StartAtStep.LiteralAsInt());
    }

    [Fact]
    public void Retake_ignored_when_clip_is_not_sourced()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        // Retake window present in the clip JSON but no clip source video => retake never activates.
        string stagesJson = JsonSingleClipStagesWithRetake(
            startSeconds: 1.0, lengthSeconds: 1.0, strength: 0.8, sourced: false,
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VideoFrames, 97);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: RetakeFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<LTXVSetVideoLatentNoiseMasksNode>());
    }
}
