using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Planning;
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
            features: [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
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
            features: [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
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
            features: [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        // The refine-skipped stage 0 is a passthrough with no sampler; stage 1 refines the
        // source footage directly.
        SwarmKSamplerNode refine = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Equal((int)Math.Floor(stage1Steps * 0.5), refine.StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, refine.LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 1 sampler latent input does not trace upstream to the SwarmLoadVideoB64 node.");
    }

    [Fact]
    public void Refine_source_video_replaces_t2v_root_and_is_the_only_published_output()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        string stagesJson = JsonSingleClipStages(
            MakeStage(models.VideoModel.Name, "Generated", steps: 10),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 12));
        T2IParamInput input = BuildTextToVideoInput(models.VideoModel, stagesJson);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0x52, 0x45, 0x46, 0x49, 0x4E, 0x45], MediaType.VideoMp4));

        // The native T2V fixture authors a sampler, decode and save before VideoStages runs. Global
        // refine must replace that whole root component, retarget the one publication to the
        // uploaded video's refine chain, and leave no stale root sampler or save.
        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeTextToVideoStepsWithPreCoreVideo(attachAudioToCurrentMedia: true),
            features: [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(
            bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.False(workflow.ContainsKey("200"), "The displaced native T2V root sampler survived.");
        Assert.False(workflow.ContainsKey("201"), "The displaced native T2V AV separator survived.");
        Assert.False(workflow.ContainsKey("202"), "The displaced native T2V video decode survived.");
        SwarmKSamplerNode refine = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.NotEqual("200", refine.Id);
        Assert.True(
            ReachesUpstream(bridge, refine, loadVideo.Id),
            "The surviving stage sampler does not refine the uploaded global source video.");

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.True(
            ReachesUpstream(bridge, save.Images.Connection!.Node, loadVideo.Id),
            "The sole save does not publish the uploaded global-refine timeline.");
        Assert.True(JToken.DeepEquals(
            WorkflowBridge.ToPath(save.Images.Connection),
            generator.CurrentMedia.Path));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Refine_source_video_skip_two_emits_no_samplers_for_the_skipped_stages()
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
            features: [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        // Both refine-skipped stages are samplerless passthroughs; only stage 2 samples, straight
        // off the source footage.
        SwarmKSamplerNode refine = Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Equal((int)Math.Floor(stage2Steps * 0.5), refine.StartAtStep.LiteralAsInt());
        Assert.True(
            ReachesUpstream(bridge, refine.LatentImage.Connection!.Node, loadVideo.Id),
            "Stage 2 sampler latent input does not trace upstream to the SwarmLoadVideoB64 node.");
    }

    private static WorkflowGenerator.WorkflowGenStep SeedAceStepFunAudioTrackStep(int trackIndex) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            bridge.AddNode(new VAEDecodeAudioNode(), AudioHandler.MakeAceStepFunDecodeId(trackIndex));
        }, 11.05);

    [Fact]
    public void Refine_source_video_with_clip_length_from_audio_drives_frames_from_audio_not_default()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        const int stage0Steps = 10;
        const int stage1Steps = 12;
        JObject clip = MakeClip(
            MakeStage(models.VideoModel.Name, "Generated", steps: stage0Steps),
            MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: stage1Steps));
        clip["AudioSource"] = "audio0";
        clip["ClipLengthFromAudio"] = true;
        string stagesJson = new JArray(clip).ToString();

        T2IParamInput input = BuildNativeInput(models.BaseModel, models.VideoModel, stagesJson);
        input.Set(
            VideoStagesExtension.RefineSourceVideo,
            new Image([0xDE, 0xAD, 0xBE, 0xEF], MediaType.VideoMp4));

        (JObject workflow, WorkflowGenerator _generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false).Append(SeedAceStepFunAudioTrackStep(0)),
            features: [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmLoadVideoB64Node loadVideo = Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());

        SwarmAudioLengthToFramesNode lengthToFrames = Assert.Single(
            bridge.Graph.NodesOfType<SwarmAudioLengthToFramesNode>());

        ImageFromBatchNode fromBatch = Assert.Single(bridge.Graph.NodesOfType<ImageFromBatchNode>(), n => ReachesUpstream(bridge, n, loadVideo.Id));
        Assert.Same(lengthToFrames.Frames, fromBatch.Length.Connection);
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

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            BuildNativeSteps(attachAudioToCurrentMedia: false),
            features: [Ltx2HostIntegration.FeatureFlag, "variation_seed", "comfy_loadimage_b64"]);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        StageSpec stage = Assert.Single(Assert.Single(generator.GetVideoStagesSpec().Clips).Stages);
        Assert.NotEqual(0, stage.Control);
        StagePlan plannedStage = Assert.Single(
            Assert.Single(
                generator.RequireVideoExecutionPlanContext().Plan.Clips)
            .Stages);
        Assert.False(plannedStage.IsPassthrough);
    }
}
