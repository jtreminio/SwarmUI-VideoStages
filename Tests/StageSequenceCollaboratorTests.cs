using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Execution;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class StageSequenceCollaboratorTests
{
    [Fact]
    public void Host_override_scope_removes_temporary_section_on_exception()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name)));
        (Newtonsoft.Json.Linq.JObject unusedWorkflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            WorkflowTestHarness.Template_BaseOnlyImage());
        VideoStagesSpec spec = generator.GetVideoStagesSpec();
        VideoExecutionPlan plan = TestPlanCompiler.Compile(spec);
        ClipPlan plannedClip = Assert.Single(plan.Clips);
        StagePlan plannedStage = Assert.Single(plannedClip.Stages);
        ClipContext clipContext = new(
            plan,
            plannedClip,
            generator.CurrentMedia,
            generator.CurrentVae);
        int sectionId = VideoStagesExtension.SectionIdForStage(plannedStage.StageId);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using StageHostExecutionScope scope = new(
                generator,
                plan);
            Assert.Equal(
                sectionId,
                scope.ApplyStageOverrides(
                    plannedClip,
                    plannedStage,
                    clipContext.Dimensions.Width,
                    clipContext.Dimensions.Height));
            Assert.True(generator.UserInput.SectionParamOverrides.ContainsKey(sectionId));
            throw new InvalidOperationException("simulated stage failure");
        }));

        Assert.False(generator.UserInput.SectionParamOverrides.ContainsKey(sectionId));
    }

    [Fact]
    public void Continuity_builder_consumes_clip_and_stage_plans()
    {
        Type[] parameterTypes = typeof(ContinuityGuideBuilder)
            .GetMethod(nameof(ContinuityGuideBuilder.TryBuild))
            ?.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            [
                typeof(ClipPlan),
                typeof(WGNodeData),
                typeof(int),
                typeof(TimelineGeometry)
            ],
            parameterTypes);
        Assert.DoesNotContain(typeof(ClipSpec), parameterTypes);
    }

    [Fact]
    public void Continuity_builder_creates_the_planned_tail_window()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name)));
        (Newtonsoft.Json.Linq.JObject unusedWorkflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            WorkflowTestHarness.Template_BaseOnlyImage());
        VideoExecutionPlan plan = TestPlanCompiler.Compile(generator.GetVideoStagesSpec());
        ClipPlan nextClip = Assert.Single(plan.Clips);
        ClipPlan previousClip = nextClip with { ClipId = 41, Frames = 16 };
        WGNodeData previousOutput = generator.CurrentMedia.Duplicate();
        previousOutput.Frames = 16;

        WGNodeData guide = new ContinuityGuideBuilder(generator).TryBuild(
            previousClip,
            previousOutput,
            window: 9,
            new TimelineGeometry(plan.Width, plan.Height, plan.FramesPerSecond));

        Assert.NotNull(guide);
        Assert.Equal(9, guide.Frames);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        ImageFromBatchNode tail = Assert.Single(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.Equal(7, tail.BatchIndex.LiteralAsInt());
        Assert.Equal(9, tail.Length.LiteralAsInt());
    }

    [Fact]
    public void Continuity_builder_conforms_the_tail_to_the_next_clips_frame_rate_only()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name)));
        (Newtonsoft.Json.Linq.JObject unusedWorkflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            WorkflowTestHarness.Template_BaseOnlyImage());
        VideoExecutionPlan plan = TestPlanCompiler.Compile(generator.GetVideoStagesSpec());
        ClipPlan nextClip = Assert.Single(plan.Clips);
        ClipPlan previousClip = nextClip with { ClipId = 41, Frames = 16 };
        WGNodeData previousOutput = generator.CurrentMedia.Duplicate();
        previousOutput.Frames = 16;
        previousOutput.Width = 512;
        previousOutput.Height = 512;
        previousOutput.FPS = new Newtonsoft.Json.Linq.JValue(24);

        WGNodeData guide = new ContinuityGuideBuilder(generator).TryBuild(
            previousClip,
            previousOutput,
            window: 4,
            new TimelineGeometry(1024, 1024, 12));

        Assert.NotNull(guide);
        // Four frames at 12 fps span eight source frames at 24 fps.
        Assert.Equal(4, guide.Frames);
        // Spatial conform happens at the consuming stage, so the tail keeps its source resolution.
        Assert.Equal(512, guide.Width);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        ImageFromBatchNode tail = Assert.Single(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.Equal(8, tail.BatchIndex.LiteralAsInt());
        Assert.Equal(8, tail.Length.LiteralAsInt());
        SwarmVideoResampleFPSNode resample =
            Assert.Single(bridge.Graph.NodesOfType<SwarmVideoResampleFPSNode>());
        Assert.Equal(tail.Id, resample.ImagesInput.Connection!.Node.Id);
        Assert.Equal(24.0, resample.FpsIn.LiteralAsDouble());
        Assert.Equal(12.0, resample.FpsOut.LiteralAsDouble());
        Assert.Empty(bridge.Graph.NodesOfType<ImageScaleNode>());
        Assert.Equal(new Newtonsoft.Json.Linq.JArray(resample.Id, 0), guide.Path);
    }

    [Fact]
    public void Guide_reference_state_resets_captured_stage_outputs()
    {
        using SwarmUiTestContext testContext = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name)));
        (Newtonsoft.Json.Linq.JObject unusedWorkflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
            input,
            WorkflowTestHarness.Template_BaseOnlyImage());
        StageRefStore store = new(generator);
        Base2EditPublishedStageRefs base2Edit = new(generator);
        StageGuideReferenceState state = new(generator, store, base2Edit);
        StagePlan stage = Assert.Single(
            Assert.Single(
                TestPlanCompiler.Compile(generator.GetVideoStagesSpec()).Clips)
            .Stages);
        StagePlan explicitStageGuide = stage with
        {
            ArchitecturePayload = stage.RequireLtx2Payload() with
            {
                Guide = new GuideReferencePlan(
                    StageGuideReferenceKind.ExplicitStage,
                    $"Stage{stage.StageId}",
                    stage.StageId)
            }
        };

        state.CaptureStageOutput(stage);
        Assert.NotNull(state.Resolve(explicitStageGuide));

        state.BeginClip();

        Assert.Null(state.Resolve(explicitStageGuide));
    }
}
