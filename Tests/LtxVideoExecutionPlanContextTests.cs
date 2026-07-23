using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class LtxVideoExecutionPlanContextTests
{
    [Fact]
    public void GetLtxPlanContext_CachesOneImmutablePlanAndRecordsRootParity()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        WorkflowGenerator generator = CreateGenerator(input);

        LtxVideoExecutionPlanContext first = generator.GetLtxVideoExecutionPlanContext()
            ?? throw new InvalidOperationException("Expected an LTX plan context.");
        LtxVideoExecutionPlanContext second = generator.GetLtxVideoExecutionPlanContext();

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(first.Plan.Clips);
        Assert.Equal(first.LegacyRootInterception, first.PlanRequestsRootInterception);
        Assert.True(first.HasRootInterceptionParity);
    }

    [Fact]
    public void GetLtxPlanContext_LeavesNonLtxTimelinesOutsideTheNewSeam()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
        WorkflowGenerator generator = CreateGenerator(input);

        Assert.Null(generator.GetLtxVideoExecutionPlanContext());
    }

    [Fact]
    public void GetLtxPlanContext_LeavesMixedModelTimelinesOutsideTheNewSeam()
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

        Assert.Null(generator.GetLtxVideoExecutionPlanContext());
    }

    [Fact]
    public void GetLtxPlanContext_UsesHostVideoModelForSourcedOnlyLtxTimeline()
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
        Assert.True(VideoStageModelCompat.IsLtxV2VideoModel(hostModel));

        LtxVideoExecutionPlanContext context = generator.GetLtxVideoExecutionPlanContext()
            ?? throw new InvalidOperationException("Expected an LTX sourced-only plan context.");

        ClipPlan clip = Assert.Single(context.Plan.Clips);
        Assert.True(clip.IsSourced);
        Assert.Empty(clip.Stages);
    }

    [Fact]
    public void GetLtxPlanContext_LeavesSourcedOnlyWanTimelineOutsideTheNewSeam()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            SourcedOnlyConfig());

        Assert.Null(CreateGenerator(input).GetLtxVideoExecutionPlanContext());
    }

    private static string SourcedOnlyConfig() => MakeRootConfig(new JObject
    {
        ["Name"] = "Sourced only",
        ["Duration"] = 0.6,
        ["Stages"] = new JArray(),
        ["SourceVideo"] = new JObject
        {
            ["Data"] = "data:video/mp4;base64,QUJD",
            ["FileName"] = "source.mp4",
            ["StartSeconds"] = 0
        }
    }).ToString();

    private static WorkflowGenerator CreateGenerator(T2IParamInput input) => new()
    {
        UserInput = input,
        Features = [],
        ModelFolderFormat = "/"
    };
}
