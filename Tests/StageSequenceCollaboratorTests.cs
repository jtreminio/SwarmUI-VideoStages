using ComfyTyped.Core;
using ComfyTyped.Generated;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures.Ltx2;
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
                plan,
                parallelMultiClip: false);
            Assert.Equal(
                sectionId,
                scope.ApplyStageOverrides(clipContext, plannedClip, plannedStage));
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
            [typeof(ClipPlan), typeof(WGNodeData), typeof(ClipPlan), typeof(int)],
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
            nextClip,
            window: 9);

        Assert.NotNull(guide);
        Assert.Equal(9, guide.Frames);
        using WorkflowBridge bridge = WorkflowBridge.Create(generator.Workflow);
        ImageFromBatchNode tail = Assert.Single(bridge.Graph.NodesOfType<ImageFromBatchNode>());
        Assert.Equal(7, tail.BatchIndex.LiteralAsInt());
        Assert.Equal(9, tail.Length.LiteralAsInt());
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
                    GuideReferenceKind.ExplicitStage,
                    $"Stage{stage.StageId}",
                    stage.StageId)
            }
        };

        state.CaptureStageOutput(stage);
        Assert.NotNull(state.Resolve(explicitStageGuide));

        state.Reset();

        Assert.Null(state.Resolve(explicitStageGuide));
    }
}
