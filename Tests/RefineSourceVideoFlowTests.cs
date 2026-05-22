using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Refine_source_video_installs_swarm_load_video_b64_into_graph()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        byte[] videoBytes = [0x52, 0x45, 0x46, 0x49, 0x4E, 0x45]; 
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image(videoBytes, MediaType.VideoMp4));

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        string base64 = loadVideo.VideoBase64.LiteralAsString();
        Assert.False(string.IsNullOrEmpty(base64));
        byte[] decoded = Convert.FromBase64String(base64);
        Assert.Equal(videoBytes, decoded);
    }

    [Fact]
    public void Refine_source_video_disabled_when_no_param_set()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
    }

    [Fact]
    public void Refine_source_video_two_stage_spec_skips_stage0_sampler_and_chains_into_stage1()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        const int stage0Steps = 10;
        const int stage1Steps = 12;
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: stage0Steps),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: stage1Steps));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0xDE, 0xAD, 0xBE, 0xEF], MediaType.VideoMp4));

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);

        Assert.Equal(stage0Steps, samplers[0].StartAtStep.LiteralAsInt());

        Assert.True(
            ReachesUpstream(bridge, samplers[0].LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 0 sampler latent input does not trace upstream to the SwarmLoadVideoB64 node.");

        Assert.True(
            ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, samplers[0].Id),
            "Stage 1 sampler latent input does not chain back to stage 0's sampler output.");

        Assert.Equal((int)Math.Floor(stage1Steps * 0.5), samplers[1].StartAtStep.LiteralAsInt());
    }

    [Fact]
    public void Refine_source_video_skip_two_passes_through_first_two_stage_samplers()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        const int stage0Steps = 10;
        const int stage1Steps = 11;
        const int stage2Steps = 12;
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: stage0Steps),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: stage1Steps),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: stage2Steps));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0xDE, 0xAD, 0xBE, 0xEF], MediaType.VideoMp4));
        input.Set(VideoStagesExtension.RefineSkipStages, 2);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(3, samplers.Count);

        Assert.Equal(stage0Steps, samplers[0].StartAtStep.LiteralAsInt());
        Assert.Equal(stage1Steps, samplers[1].StartAtStep.LiteralAsInt());

        Assert.True(
            ReachesUpstream(bridge, samplers[0].LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 0 sampler latent input does not trace upstream to the SwarmLoadVideoB64 node.");
        Assert.True(
            ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 1 sampler latent input does not trace upstream to the SwarmLoadVideoB64 node.");

        Assert.Equal((int)Math.Floor(stage2Steps * 0.5), samplers[2].StartAtStep.LiteralAsInt());
    }

    [Fact]
    public void Refine_source_video_with_wan_model_skips_stage0_and_chains_into_stage1()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        const int stage0Steps = 10;
        const int stage1Steps = 12;
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: stage0Steps),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: stage1Steps));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0xDE, 0xAD, 0xBE, 0xEF], MediaType.VideoMp4));

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps(),
            features: [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);

        Assert.Equal(stage0Steps, samplers[0].StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, samplers[0].LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 0 sampler latent input does not trace upstream to the SwarmLoadVideoB64 node on WAN.");
        Assert.True(
            ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, samplers[0].Id),
            "Stage 1 sampler latent input does not chain back to stage 0's sampler output on WAN.");
        Assert.Equal((int)Math.Floor(stage1Steps * 0.5), samplers[1].StartAtStep.LiteralAsInt());
    }

    [Fact]
    public void Refine_source_video_with_wan_model_skip_two_passes_through_first_two_samplers()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22_14bImage2VideoModels();

        const int stage0Steps = 10;
        const int stage1Steps = 11;
        const int stage2Steps = 12;
        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: stage0Steps),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: stage1Steps),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: stage2Steps));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(T2IParamTypes.VAE, models.BaseModel);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0xDE, 0xAD, 0xBE, 0xEF], MediaType.VideoMp4));
        input.Set(VideoStagesExtension.RefineSkipStages, 2);

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildCoreVideoWorkflowSteps(),
            features: [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(3, samplers.Count);

        Assert.Equal(stage0Steps, samplers[0].StartAtStep.LiteralAsInt());
        Assert.Equal(stage1Steps, samplers[1].StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, samplers[0].LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 0 sampler latent does not trace upstream to the SwarmLoadVideoB64 node on WAN.");
        Assert.True(
            ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 1 sampler latent does not trace upstream to the SwarmLoadVideoB64 node on WAN.");
        Assert.Equal((int)Math.Floor(stage2Steps * 0.5), samplers[2].StartAtStep.LiteralAsInt());
    }

    [Fact]
    public void Refine_source_video_ignored_when_media_is_not_video_type()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10));

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0xFF], MediaType.ImagePng));

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: [Constants.LtxVideoFeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
    }
}
