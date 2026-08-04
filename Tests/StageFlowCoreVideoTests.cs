using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.HostVideo;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;

// What is left of the core-video stub flow after the graph conversion (see
// Ltx2CoreVideoContractTests): the cases the real Comfy API POST path cannot reach. Two need a
// sibling extension's AceStepFun VAEDecodeAudio at its reserved node id, which no core or
// VideoStages step produces; the rest assert plan-level or generator state with no graph
// counterpart, or need a model fixture that classifies as SVD.
// A file comment, not an XML doc: StageFlowTests is a 13-file partial type.

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Clip_shaped_json_without_executable_timeline_does_not_gate_core_generation()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();

        string stagesJson = new JArray(
            MakeClip()
        ).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject _workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildCoreVideoWorkflowSteps());

        Assert.Null(generator.GetVideoExecutionPlanContext());
        Assert.NotNull(generator.CurrentMedia);
    }

    [Fact]
    public void Ltx_chained_pixel_upscale_reuses_prior_audio_latent_without_redriving_length()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject stage0 = MakeStage(models.VideoModel.Name, "Generated", steps: 8);
        JObject stage1 = MakeStage(
            models.VideoModel.Name,
            "PreviousStage",
            upscale: 1.5,
            upscaleMethod: "pixel-lanczos",
            steps: 8);
        JObject clip = MakeClipWithRefs(stages: [stage0, stage1]);
        clip["audioSource"] = "audio0";
        clip["clipLengthFromAudio"] = true;
        string stagesJson = MakeRootConfig(width: 512, height: 512, clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps().Append(SeedAceStepFunAudioTrackStep(0)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Reusing the retargeted post-chain audio decode for length would create a graph cycle.
        Assert.DoesNotContain(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>(),
            node => node.AudioInput.Connection!.Node is LTXVAudioVAEDecodeNode);
        foreach (SwarmKSamplerNode sampler in bridge.Graph.NodesOfType<SwarmKSamplerNode>())
        {
            Assert.False(
                bridge.Graph.IsReachableUpstream(sampler, sampler.Id),
                $"Sampler {sampler.Id} is reachable from its own inputs — a stage feeds its output back into itself.");
        }
    }

    [Fact]
    public void Ltx_stage_pixel_upscale_does_not_feed_stage_output_back_into_its_own_input()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 8),
            MakeStage(
                models.VideoModel.Name,
                "PreviousStage",
                control: 0.5,
                upscale: 1.5,
                upscaleMethod: "pixel-lanczos",
                steps: 8));
        clip["audioSource"] = "audio0";
        clip["clipLengthFromAudio"] = true;
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeStepsWithTrimWrapper(attachAudioToCurrentMedia: true).Append(SeedAceStepFunAudioTrackStep(0)));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        // Retargeted post-chain decodes must not feed the pixel-upscale stage input.
        foreach (ComfyNode node in bridge.Graph.NodesOfType<SwarmKSamplerNode>())
        {
            Assert.False(
                bridge.Graph.IsReachableUpstream(node, node.Id),
                $"Sampler {node.Id} is reachable from its own inputs — the stage feeds its output back into itself.");
        }
    }

    private static WorkflowGenerator.WorkflowGenStep SeedAceStepFunAudioTrackStep(int trackIndex) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(trackIndex));
        }, 11.05);
}
