using ComfyTyped.Core;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Execution;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

public class StageExecutionAdapterTests
{
    [Fact]
    public void Execute_publishes_and_returns_artifacts_that_chain_between_typed_stages()
    {
        JObject workflow = [];
        WorkflowGenerator generator = new() { Workflow = workflow, UserInput = new(null) };
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        _ = bridge.AddStub("UnitTestInput", "10").WithOutputs(WGNodeData.DT_VIDEO);
        _ = bridge.AddStub("UnitTestFirst", "11").WithOutputs(WGNodeData.DT_VIDEO);
        _ = bridge.AddStub("UnitTestSecond", "12").WithOutputs(WGNodeData.DT_VIDEO);
        _ = bridge.AddStub("UnitTestVae", "13").WithOutputs(WGNodeData.DT_VAE);
        generator.CurrentMedia = Data(generator, "10", WGNodeData.DT_VIDEO);
        generator.CurrentVae = Data(generator, "13", WGNodeData.DT_VAE);

        RuntimeArtifact input = RuntimeArtifact.Capture(generator, bridge, ArtifactOrigin.HostRoot);
        generator.CurrentMedia = null;
        generator.CurrentVae = null;
        ClipSpec clip = Clip();
        StagePlan stage = VideoExecutionPlanCompiler.Compile(new VideoStagesSpec(512, 512, 24, false, [clip]))
            .Clips[0].Stages[0];
        RecordingStageRunner legacy = new(generator, Data(generator, "11", WGNodeData.DT_VIDEO), Data(generator, "12", WGNodeData.DT_VIDEO));
        StageExecutionAdapter adapter = new(generator, legacy);
        StageExecutionAdapterContext context = new(
            GuideReference: null,
            ReferenceStore: new StageRefStore(generator),
            ClipContext: new ClipContext(clip, 512, 512, Data(generator, "10", WGNodeData.DT_VIDEO), Data(generator, "13", WGNodeData.DT_VAE)),
            IsParallelMultiClip: true,
            TotalClipCount: 2,
            ClipIndex: 1,
            ClipStageIndex: 0);

        RuntimeArtifact first = adapter.Execute(stage, 500, context, input);
        RuntimeArtifact second = adapter.Execute(stage, 501, context, first);

        Assert.Equal(2, legacy.CallCount);
        Assert.Equal("10", legacy.InputPaths[0]);
        Assert.Equal("11", legacy.InputPaths[1]);
        Assert.All(legacy.Stages, seen =>
        {
            Assert.Equal(stage.Core.Model, seen.Model);
            Assert.Equal(stage.Core.Steps, seen.Steps);
            Assert.Equal(stage.Core.Control, seen.Control);
        });
        Assert.Equal(ArtifactOrigin.StageOutput, first.Origin);
        Assert.Equal(ArtifactOrigin.StageOutput, second.Origin);
        Assert.Equal("12", second.Media.Output.Node.Id);
        Assert.Equal("12", $"{generator.CurrentMedia.Path[0]}");
    }

    [Fact]
    public void ResolvePlannedClips_uses_clip_and_stage_positions_not_global_stage_id_uniqueness()
    {
        ClipSpec first = Clip(clipId: 0, stageId: 7);
        ClipSpec second = Clip(clipId: 1, stageId: 7);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            new VideoStagesSpec(512, 512, 24, false, [first, second]));

        IReadOnlyList<ClipPlan> resolved = StageSequenceRunner.ResolvePlannedClips(plan, [first, second]);

        Assert.NotNull(resolved);
        Assert.Equal(2, resolved.Count);
        Assert.Equal(7, resolved[0].Stages[0].StageId);
        Assert.Equal(7, resolved[1].Stages[0].StageId);
    }

    [Fact]
    public void ResolvePlannedClips_returns_legacy_fallback_for_mismatched_stage_sequences()
    {
        ClipSpec planned = Clip(clipId: 0, stageId: 7);
        ClipSpec changed = Clip(clipId: 0, stageId: 8);
        VideoExecutionPlan plan = VideoExecutionPlanCompiler.Compile(
            new VideoStagesSpec(512, 512, 24, false, [planned]));

        Assert.Null(StageSequenceRunner.ResolvePlannedClips(plan, [changed]));
    }

    private static ClipSpec Clip(int clipId = 0, int stageId = 0)
    {
        StageSpec stage = new(
            Id: stageId,
            Control: 0.5,
            Upscale: 1,
            UpscaleMethod: "pixel-lanczos",
            Model: "ltx-2",
            Steps: 8,
            CfgScale: 4.5,
            Sampler: "euler",
            Scheduler: "normal",
            ImageReference: "Generated",
            ClipStageIndex: 0,
            ClipStageRawIndex: 0);
        return new ClipSpec(clipId, 49, Constants.AudioSourceNative, [], false, false, false, false, null, [], [stage]);
    }

    private static WGNodeData Data(WorkflowGenerator generator, string nodeId, string dataType) =>
        new(new JArray(nodeId, 0), generator, dataType, T2IModelClassSorter.CompatLtxv2);

    private sealed class RecordingStageRunner : StageRunner
    {
        private readonly WorkflowGenerator _generator;
        private readonly Queue<WGNodeData> _outputs;

        public RecordingStageRunner(WorkflowGenerator generator, params WGNodeData[] outputs)
            : base(generator, null, null)
        {
            _generator = generator;
            _outputs = new Queue<WGNodeData>(outputs);
        }

        public int CallCount { get; private set; }

        public List<string> InputPaths { get; } = [];

        public List<StageSpec> Stages { get; } = [];

        public override void RunStage(
            StageSpec stage,
            int sectionId,
            StageRefStore.StageRef guideReference,
            StageRefStore refStore,
            ClipContext clipContext)
        {
            CallCount++;
            InputPaths.Add($"{_generator.CurrentMedia.Path[0]}");
            Stages.Add(stage);
            _generator.CurrentMedia = _outputs.Dequeue();
        }
    }
}
