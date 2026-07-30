using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Generated;
using Xunit;
using static VideoStages.Tests.Fixtures;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

public partial class StageFlowTests
{
    [Fact]
    public void Plan_backed_sourced_only_ltx_clip_publishes_conformed_footage_without_a_sampler()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject sourced = MakeSourcedClip(models);
        sourced["stages"] = new JArray();

        (JObject workflow, WorkflowGenerator generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window =
            AssertSourcedConformChain(bridge, SourcedOnlyClipFrames);
        Assert.Empty(SamplerNodesOrdered(bridge));
        INodeOutput currentOutput = bridge.ResolvePath((JArray)generator.CurrentMedia.Path);
        Assert.True(ReachesUpstream(bridge, currentOutput.Node, window.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_only_clip_interpolates_once_and_preserves_its_audio_at_publication()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject sourced = MakeSourcedClip(models);
        sourced["stages"] = new JArray();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(512, 512, sourced).ToString());
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, 2);
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMethod, "RIFE");

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true),
                features: SourcedClipFeatures.Concat(["frameinterps"]));
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode sourceWindow =
            AssertSourcedConformChain(bridge, SourcedOnlyClipFrames);
        ComfyNode rife = Assert.Single(
            bridge.Graph.Nodes.Values,
            node => node.ClassTypeName == "RIFE VFI");
        Assert.True(ReachesUpstream(bridge, rife, sourceWindow.Id));
        Assert.Equal(SourcedOnlyClipFrames * 2 - 1, generator.CurrentMedia.Frames);
        Assert.Equal(48, generator.CurrentMedia.GetRawFPS());
        Assert.Equal(new JArray(rife.Id, 0), generator.CurrentMedia.Path);

        WGNodeData attachedAudio = Assert.IsType<WGNodeData>(
            generator.CurrentMedia.AttachedAudio);
        INodeOutput attachedAudioOutput = bridge.ResolvePath(attachedAudio.Path);
        Assert.NotNull(attachedAudioOutput);
        TrimAudioDurationNode sourceAudioTrim = Assert.Single(
            bridge.Graph.NodesOfType<TrimAudioDurationNode>());
        Assert.True(ReachesUpstream(bridge, attachedAudioOutput.Node, sourceAudioTrim.Id));

        SwarmSaveAnimationWSNode save = Assert.Single(
            bridge.Graph.NodesOfType<SwarmSaveAnimationWSNode>());
        Assert.Same(rife, save.Images.Connection?.Node);
        Assert.Equal(48.0, save.Fps.LiteralAsDouble());
        Assert.True(JToken.DeepEquals(
            attachedAudio.Path,
            WorkflowBridge.ToPath(save.Audio.Connection!)));

        Assert.Empty(SamplerNodesOrdered(bridge));
        Assert.DoesNotContain(
            new[] { "200", "201", "202", "203" },
            workflow.ContainsKey);
        AssertNoDanglingNodeRefs(workflow);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_only_clip_executes_planned_audio_segments_without_a_sampler()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject sourced = MakeSourcedClip(models);
        sourced["stages"] = new JArray();
        JObject root = MakeRootConfig(512, 512, sourced);
        root["audioTracks"] = new JArray(new JObject
        {
            ["id"] = "track-overlay",
            ["source"] = new JObject
            {
                ["kind"] = "Upload",
                ["reference"] = "overlay.wav",
                ["uploadedAudio"] = new JObject
                {
                    ["data"] = "data:audio/wav;base64,QUJD",
                    ["fileName"] = "overlay.wav",
                },
            },
            ["spans"] = new JArray(new JObject
            {
                ["timelineStartSeconds"] = 0.1,
                ["timelineLengthSeconds"] = 0.2,
                ["sourceStartSeconds"] = 0.0,
            }),
        });

        (JObject workflow, WorkflowGenerator generator) = GenerateSourcedRootFlow(models, root);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(SamplerNodesOrdered(bridge));
        AudioMergeNode merge = Assert.Single(bridge.Graph.NodesOfType<AudioMergeNode>());
        Assert.Equal("add", merge.MergeMethod.LiteralAsString());
        Assert.Equal(new JArray(merge.Id, 0), generator.CurrentMedia.AttachedAudio.Path);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_only_single_clip_trims_video_and_audio_once_at_the_terminal_boundary()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject sourced = MakeSourcedClip(models);
        sourced["stages"] = new JArray();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(512, 512, sourced).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 2);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 3);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true),
                features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(SamplerNodesOrdered(bridge));
        SwarmTrimFramesNode videoTrim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.Equal(new JArray(videoTrim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(SourcedOnlyClipFrames - 5, generator.CurrentMedia.Frames);

        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Equal(2 / 24.0, audioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(
            (SourcedOnlyClipFrames - 5) / 24.0,
            audioTrim.Duration.LiteralAsDouble()!.Value,
            precision: 6);
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_only_multi_clip_timeline_assembles_then_trims_video_and_audio_once()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject first = MakeSourcedClip(models);
        first["stages"] = new JArray();
        JObject second = MakeSourcedClip(models);
        second["stages"] = new JArray();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeRootConfig(512, 512, first, second).ToString());
        input.Set(T2IParamTypes.TrimVideoStartFrames, 1);
        input.Set(T2IParamTypes.TrimVideoEndFrames, 2);

        (JObject workflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                BuildNativeSteps(attachAudioToCurrentMedia: true),
                features: SourcedClipFeatures);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        Assert.Empty(SamplerNodesOrdered(bridge));
        BatchImagesNodeNode merge = Assert.Single(bridge.Graph.NodesOfType<BatchImagesNodeNode>());
        SwarmTrimFramesNode videoTrim = Assert.Single(
            bridge.Graph.NodesOfType<SwarmTrimFramesNode>());
        Assert.True(ReachesUpstream(bridge, videoTrim.Image.Connection!.Node, merge.Id));
        Assert.Equal(new JArray(videoTrim.Id, 0), generator.CurrentMedia.Path);
        Assert.Equal(SourcedOnlyClipFrames * 2 - 3, generator.CurrentMedia.Frames);

        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        AudioConcatNode audioConcat = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());
        Assert.True(ReachesUpstream(bridge, audioTrim, audioConcat.Id));
        Assert.Equal(1 / 24.0, audioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(
            (SourcedOnlyClipFrames * 2 - 3) / 24.0,
            audioTrim.Duration.LiteralAsDouble()!.Value,
            precision: 6);
        AssertWorkflowHasNoCycles(workflow);
    }
}
