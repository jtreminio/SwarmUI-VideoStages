using ComfyTyped.Core;
using ComfyTyped.Generated;
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
        sourced["Stages"] = new JArray();

        (JObject workflow, WorkflowGenerator generator) = GenerateSourcedFlow(models, sourced);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        SwarmFrameWindowNode window = AssertSourcedConformChain(bridge);
        Assert.Empty(SamplerNodesOrdered(bridge));
        INodeOutput currentOutput = bridge.ResolvePath((JArray)generator.CurrentMedia.Path);
        Assert.True(ReachesUpstream(bridge, currentOutput.Node, window.Id));
        AssertWorkflowHasNoCycles(workflow);
    }

    [Fact]
    public void Sourced_only_clip_executes_planned_audio_segments_without_a_sampler()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        JObject sourced = MakeSourcedClip(models);
        sourced["Stages"] = new JArray();
        sourced["AudioSegments"] = new JArray(new JObject
        {
            ["StartSeconds"] = 0.1,
            ["TrimStartSeconds"] = 0.0,
            ["LengthSeconds"] = 0.2,
            ["Source"] = new JObject
            {
                ["Data"] = "data:audio/wav;base64,QUJD",
                ["FileName"] = "overlay.wav",
            },
        });

        (JObject workflow, WorkflowGenerator generator) = GenerateSourcedFlow(models, sourced);
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
        sourced["Stages"] = new JArray();
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
        Assert.Equal(SourcedClipFrames - 5, generator.CurrentMedia.Frames);

        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        Assert.Equal(2 / 24.0, audioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(
            (SourcedClipFrames - 5) / 24.0,
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
        first["Stages"] = new JArray();
        JObject second = MakeSourcedClip(models);
        second["Stages"] = new JArray();
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
        Assert.Equal(SourcedClipFrames * 2 - 3, generator.CurrentMedia.Frames);

        TrimAudioDurationNode audioTrim = Assert.IsType<TrimAudioDurationNode>(
            bridge.ResolvePath(generator.CurrentMedia.AttachedAudio.Path).Node);
        AudioConcatNode audioConcat = Assert.Single(bridge.Graph.NodesOfType<AudioConcatNode>());
        Assert.True(ReachesUpstream(bridge, audioTrim, audioConcat.Id));
        Assert.Equal(1 / 24.0, audioTrim.StartIndex.LiteralAsDouble()!.Value, precision: 6);
        Assert.Equal(
            (SourcedClipFrames * 2 - 3) / 24.0,
            audioTrim.Duration.LiteralAsDouble()!.Value,
            precision: 6);
        AssertWorkflowHasNoCycles(workflow);
    }
}
