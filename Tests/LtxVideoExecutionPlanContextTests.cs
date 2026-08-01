using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Ltx2;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class VideoExecutionPlanContextTests
{
    [Fact]
    public void GetPlanContext_CachesOneImmutablePlan()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        WorkflowGenerator generator = CreateGenerator(input);

        VideoExecutionPlanContext first = generator.GetVideoExecutionPlanContext()
            ?? throw new InvalidOperationException("Expected a video plan context.");
        VideoExecutionPlanContext second = generator.GetVideoExecutionPlanContext();

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(first.Plan.Clips);
        Assert.NotEqual(HostCoreDisposition.Keep, first.Plan.Root.CoreDisposition);
    }

    [Fact]
    public void PrepareRequest_is_idempotent_and_reuses_one_runtime_binding()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        WorkflowGenerator generator = CreateGenerator(input);
        VideoExecutionPlanContext context = generator.RequireVideoExecutionPlanContext();

        Assert.Equal(VideoExecutionState.Compiled, context.State);
        context.PrepareRequest();
        VideoArchitectureExecutionHost first = context.RequirePreparedExecutionHost();
        context.PrepareRequest();

        Assert.Equal(VideoExecutionState.Prepared, context.State);
        Assert.Same(first, context.RequirePreparedExecutionHost());
    }

    [Fact]
    public void Every_mutating_runner_entry_requires_completed_preflight()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        WorkflowGenerator generator = CreateGenerator(input);
        VideoExecutionPlanContext context = generator.RequireVideoExecutionPlanContext();

        Action<WorkflowGenerator>[] phases =
        [
            Runner.CaptureCoreVideoControlNetPreprocessors,
            Runner.CaptureBase,
            Runner.CaptureRefiner,
            Runner.CapturePreCoreVideoMedia,
            Runner.DropCoreImageToVideoOutput,
            Runner.ApplyRootAudioMaskDimensionsAfterNativeVideo,
            Runner.RunConfiguredStages,
            current => RootVideoStageResizer.ApplyRootResolutionBeforeImageToVideo(new()
            {
                Generator = current,
                ContextID = T2IParamInput.SectionID_Video,
            }),
        ];

        foreach (Action<WorkflowGenerator> phase in phases)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => phase(generator));
            Assert.Contains("preflight must complete first", error.Message);
        }
        Assert.Equal(VideoExecutionState.Compiled, context.State);
    }

    [Fact]
    public void Successful_production_execution_completes_and_preserves_plan_access()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        (JObject unusedWorkflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));
        VideoExecutionPlanContext context =
            generator.RequireVideoExecutionPlanContext();

        Assert.Equal(VideoExecutionState.Completed, context.State);
        Assert.Single(context.Plan.Clips);
        Assert.Throws<InvalidOperationException>(() => context.RequirePrepared());
    }

    [Fact]
    public void Blocking_plan_failure_is_memoized_as_failed()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndUnsupportedVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        VideoExecutionPlanContext context = Assert.IsType<VideoExecutionPlanContext>(
            CreateGenerator(input).GetVideoExecutionPlanContext());

        SwarmUserErrorException first = Assert.Throws<SwarmUserErrorException>(
            () => context.PrepareRequest());
        SwarmUserErrorException repeated = Assert.Throws<SwarmUserErrorException>(
            () => context.PrepareRequest());

        Assert.Same(first, repeated);
        Assert.Equal(VideoExecutionState.Failed, context.State);
    }

    [Fact]
    public void Empty_active_timeline_is_inert_across_all_registered_phases()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            "[]");

        (JObject unusedWorkflow, WorkflowGenerator generator) =
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()));

        Assert.Null(generator.GetVideoExecutionPlanContext());
        Assert.NotNull(generator.CurrentMedia);
    }

    [Fact]
    public void GetPlanContext_PreservesUnsupportedArchitectureDiagnostics()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndUnsupportedVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        WorkflowGenerator generator = CreateGenerator(input);

        VideoExecutionPlanContext context = Assert.IsType<VideoExecutionPlanContext>(
            generator.GetVideoExecutionPlanContext());
        Assert.Contains(
            context.Plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-stage0-model-unresolved");
    }

    [Fact]
    public void GetPlanContext_PreservesMixedModelDiagnostics()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated"),
                MakeStage("not-an-ltx-model", "PreviousStage")));
        WorkflowGenerator generator = CreateGenerator(input);

        VideoExecutionPlanContext context = Assert.IsType<VideoExecutionPlanContext>(
            generator.GetVideoExecutionPlanContext());
        Assert.Contains(
            context.Plan.Diagnostics,
            diagnostic => diagnostic.Code == "architecture-authored-stage-model-unresolved");
    }

    [Fact]
    public void GetPlanContext_ResolvesInitVideoOnlyTimelineAsNone()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            InitVideoOnlyConfig());
        WorkflowGenerator generator = CreateGenerator(input);

        Assert.Single(generator.GetVideoStagesSpec().Clips);
        Assert.True(generator.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel hostModel));
        Assert.True(Ltx2ModelCompatibility.IsLtxV2VideoModel(hostModel));

        VideoExecutionPlanContext context = generator.GetVideoExecutionPlanContext()
            ?? throw new InvalidOperationException("Expected a init-video-only plan context.");

        ClipPlan clip = Assert.Single(context.Plan.Clips);
        Assert.True(clip.HasInitVideo);
        Assert.Empty(clip.Stages);
        Assert.Equal(NoneArchitecture.Id, clip.Architecture.Id);
    }

    [Fact]
    public void GetPlanContext_AllowsInitVideoOnlyTimelineWithNonLtxHost()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndUnsupportedVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            InitVideoOnlyConfig());

        VideoExecutionPlanContext context = Assert.IsType<VideoExecutionPlanContext>(
            CreateGenerator(input).GetVideoExecutionPlanContext());
        Assert.Equal(NoneArchitecture.Id, Assert.Single(context.Plan.Clips).Architecture.Id);
    }

    [Fact]
    public void RequirePlan_rejects_unregistered_generated_model_with_precise_error()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndUnsupportedVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));

        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => CreateGenerator(input).RequireVideoExecutionPlanContext());

        Assert.Contains("does not resolve to a registered video architecture", error.Message);
    }

    [Fact]
    public void RequirePlan_rejects_mixed_model_timeline()
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
            () => CreateGenerator(input).RequireVideoExecutionPlanContext());

        Assert.Contains("does not resolve to a registered video architecture", error.Message);
    }

    [Fact]
    public void RequirePlan_returns_the_cached_canonical_plan()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        WorkflowGenerator generator = CreateGenerator(input);

        Assert.Same(
            generator.GetVideoExecutionPlanContext(),
            generator.RequireVideoExecutionPlanContext());
    }

    private static string InitVideoOnlyConfig() => MakeRootConfig(new JObject
    {
        ["duration"] = 0.6,
        ["stages"] = new JArray(),
        ["initVideo"] = new JObject
        {
            ["data"] = "data:video/mp4;base64,QUJD",
            ["fileName"] = "source.mp4",
            ["startSeconds"] = 0
        }
    }).ToString();

    private static WorkflowGenerator CreateGenerator(T2IParamInput input) => new()
    {
        UserInput = input,
        Features = [],
        ModelFolderFormat = "/"
    };
}
