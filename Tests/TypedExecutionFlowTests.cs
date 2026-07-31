using ComfyTyped.Core;
using ComfyTyped.Generated;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

/// <summary>
/// Characterization coverage for the plan-backed typed execution path. These cases run through
/// the canonical LTX plan and assert the required graph-level outcomes.
/// </summary>
public partial class StageFlowTests
{
    [Fact]
    public void Active_unregistered_SVD_configuration_fails_before_execution()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true)));

        Assert.Contains("does not resolve to a registered video architecture", error.Message);
    }

    [Fact]
    public void Active_mixed_model_configuration_fails_before_execution()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated"),
                MakeStage("not-an-ltx-model", "PreviousStage")));

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true)));

        Assert.Contains("does not resolve to a registered video architecture", error.Message);
    }

    [Fact]
    public void Typed_plan_single_stage_ltx_preserves_one_stage_graph()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8)));

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(SamplerNodesOrdered(bridge));
        Assert.Equal(WGNodeData.DT_VIDEO, generator.CurrentMedia.DataType);
    }

    [Fact]
    public void Typed_plan_multi_stage_ltx_preserves_stage_chaining()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));

        (JObject workflow, WorkflowGenerator unusedGenerator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        Assert.Equal(2, samplers.Count);
        Assert.True(ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, samplers[0].Id));
    }

    [Fact]
    public void Typed_plan_multi_stage_applies_global_trim_only_after_the_terminal_stage()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));
        input.Set(T2IParamTypes.TrimVideoStartFrames, 2);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 3);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<SwarmKSamplerNode> samplers = SamplerNodesOrdered(bridge);
        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.False(ReachesUpstream(bridge, samplers[1].LatentImage.Connection!.Node, trim.Id));
        Assert.True(ReachesUpstream(bridge, trim.Image.Connection!.Node, samplers[1].Id));
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(11, generator.CurrentMedia.Frames);
    }

    [Fact]
    public void Typed_plan_terminal_trim_updates_published_frame_metadata()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8)));
        input.Set(T2IParamTypes.TrimVideoStartFrames, 1);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 4);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(11, generator.CurrentMedia.Frames);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(24, generator.CurrentMedia.FPS);
    }

    [Fact]
    public void Typed_plan_init_video_ltx_preserves_source_refine_graph()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        (JObject workflow, WorkflowGenerator unusedGenerator) = GenerateInitVideoFlow(models, MakeInitVideoClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Single(bridge.Graph.NodesOfType<SwarmLoadVideoB64Node>());
        Assert.Single(SamplerNodesOrdered(bridge));
    }

    [Fact]
    public void Typed_plan_multi_clip_ltx_preserves_parallel_merge_graph()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();

        (JObject workflow, WorkflowGenerator unusedGenerator) = GenerateInitVideoFlow(
            models,
            MakeGeneratedClip(models),
            MakeGeneratedClip(models));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Equal(2, SamplerNodesOrdered(bridge).Count);
        Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
    }

    [Fact]
    public void Typed_plan_multi_clip_applies_global_trim_once_after_timeline_assembly()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(
                512,
                512,
                MakeGeneratedClip(models),
                MakeGeneratedClip(models)).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 2);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 3);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true),
                features: InitVideoClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmTrimFramesNode trim = Assert.Single(bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        Assert.True(ReachesUpstream(bridge, trim.Image.Connection!.Node, merge.Id));
        Assert.Equal(new JArray(trim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(27, generator.CurrentMedia.Frames);
        Assert.Equal(512, generator.CurrentMedia.Width);
        Assert.Equal(512, generator.CurrentMedia.Height);
        Assert.Equal(24, generator.CurrentMedia.FPS);

        WGNodeData attachedAudio = generator.CurrentMedia.AttachedAudio;
        Assert.NotNull(attachedAudio);
        string attachedAudioNodeId = $"{attachedAudio.Path[0]}";
        INodeOutput attachedAudioOutput = bridge.ResolvePath((JArray)attachedAudio.Path);
        Assert.NotNull(attachedAudioOutput);
        Assert.Contains(
            bridge.Graph.NodesOfType<AudioConcatNode>(),
            concat => ReachesUpstream(bridge, attachedAudioOutput.Node, concat.Id));

        SwarmSaveAnimationWSNode finalSave = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Equal(
            attachedAudioNodeId,
            finalSave.Audio.Connection?.Node.Id);
    }

    [Fact]
    public void Typed_plan_multi_clip_host_wrapper_is_not_reapplied_to_assembled_output()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(
                512,
                512,
                MakeGeneratedClip(models),
                MakeGeneratedClip(models)).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 2);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 3);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeStepsWithTrimWrapper(attachAudioToCurrentMedia: true),
                features: InitVideoClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        List<SwarmTrimFramesNode> outputTrims = [
            .. bridge.Graph.NodesOfType<SwarmTrimFramesNode>()
                .Where(trim => ReachesUpstream(bridge, trim.Image.Connection!.Node, merge.Id))
        ];
        SwarmTrimFramesNode timelineTrim = Assert.Single(outputTrims);
        Assert.Equal(new JArray(timelineTrim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(23, generator.CurrentMedia.Frames);
        Assert.NotNull(generator.CurrentMedia.AttachedAudio);
    }

    [Fact]
    public void Three_stage_intermediate_publications_remain_bound_to_distinct_stage_artifacts()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 9),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));
        input.Set(T2IParamTypes.OutputIntermediateImages, true);

        (JObject workflow, WorkflowGenerator generator) = WorkflowTestHarness.GenerateWithStepsAndState(
            input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        List<ComfyNode> saves = [
            .. bridge.Graph.Nodes.Values.Where(
                node => node is SwarmSaveAnimationWSNode or SwarmSaveImageWSNode)
        ];
        Assert.True(
            saves.Count == 3,
            $"Expected three publications, found {saves.Count}; intermediate flag="
            + $"{generator.UserInput.Get(T2IParamTypes.OutputIntermediateImages, false)}; "
            + $"stage count={generator.GetVideoStagesSpec().Clips.Sum(clip => clip.Stages.Count)}; "
            + $"save-like classes={string.Join(",", bridge.Graph.Nodes.Values.Where(node => node.ClassTypeName.Contains("Save")).Select(node => node.ClassTypeName))}");
        string[] publishedPaths = [
            .. saves.Select(
                save => WorkflowBridge.ToPath(save.FindInput("images").Connection!).ToString())
        ];
        Assert.Equal(3, publishedPaths.Distinct().Count());
        Assert.Single(
            saves,
            save => JToken.DeepEquals(
                WorkflowBridge.ToPath(save.FindInput("images").Connection!),
                generator.CurrentMedia.Path));
    }

    [Fact]
    public void Do_not_save_suppresses_host_and_intermediate_publications()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", control: 0.5, steps: 8),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 9),
                MakeStage(models.VideoModel.Name, "PreviousStage", control: 0.5, steps: 10)));
        input.Set(T2IParamTypes.OutputIntermediateImages, true);
        input.Set(T2IParamTypes.DoNotSave, true);

        (JObject workflow, WorkflowGenerator unusedGenerator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input, BuildNativeSteps(attachAudioToCurrentMedia: true));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.DoesNotContain(
            bridge.Graph.Nodes.Values,
            node => node is SwarmSaveAnimationWSNode or SwarmSaveImageWSNode);
    }
}
