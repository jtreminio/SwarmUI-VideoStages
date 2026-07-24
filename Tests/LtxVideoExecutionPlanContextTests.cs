using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
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
    public void GetPlanContext_PreservesUnsupportedArchitectureDiagnostics()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
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
    public void GetPlanContext_ResolvesSourcedOnlyTimelineAsNone()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            SourcedOnlyConfig());
        WorkflowGenerator generator = CreateGenerator(input);

        Assert.Single(generator.GetVideoStagesSpec().Clips);
        Assert.True(generator.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel hostModel));
        Assert.True(Ltx2ModelCompatibility.IsLtxV2VideoModel(hostModel));

        VideoExecutionPlanContext context = generator.GetVideoExecutionPlanContext()
            ?? throw new InvalidOperationException("Expected a sourced-only plan context.");

        ClipPlan clip = Assert.Single(context.Plan.Clips);
        Assert.True(clip.IsSourced);
        Assert.Empty(clip.Stages);
        Assert.Equal(NoneArchitecture.Id, clip.Architecture.Id);
    }

    [Fact]
    public void GetPlanContext_AllowsSourcedOnlyTimelineWithNonLtxHost()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            SourcedOnlyConfig());

        VideoExecutionPlanContext context = Assert.IsType<VideoExecutionPlanContext>(
            CreateGenerator(input).GetVideoExecutionPlanContext());
        Assert.Equal(NoneArchitecture.Id, Assert.Single(context.Plan.Clips).Architecture.Id);
    }

    [Fact]
    public void RequirePlan_rejects_unregistered_generated_model_with_precise_error()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
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

    private static string SourcedOnlyConfig() => MakeRootConfig(new JObject
    {
        ["duration"] = 0.6,
        ["stages"] = new JArray(),
        ["sourceVideo"] = new JObject
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
