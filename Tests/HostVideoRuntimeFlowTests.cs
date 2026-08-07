using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures.HostVideo;
using VideoStages.Planning;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// What the generic host-video fallback does under states no POST reaches: a later generic clip
/// probed against a specialized root owner, an injected core-isolation failure, and the preflight
/// diagnostics that never enter the compiled plan. The graph-observable half — including the
/// mixed-compatibility refusal and the ignored request-global settings — lives in
/// <see cref="HostVideoContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public class HostVideoRuntimeFlowTests
{
    [Fact]
    public void Later_generic_clip_does_not_change_a_specialized_root_request()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IModel hostModel = AddHostModel(
            models.VideoModel,
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5",
            "UnitTest_Later_HostVideo.safetensors");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            MakeDocument(
                MakeClip(MakeStage(models.VideoModel.Name, "Generated", steps: 8)),
                MakeClip(MakeStage(hostModel.Name, "Generated", steps: 8)))
                .ToString());
        input.Set(T2IParamTypes.VideoSwapModel, hostModel);
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.25);
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Workflow = [],
        };
        VideoExecutionPlan plan = generator.RequireVideoExecutionPlanContext().Plan;

        HostVideoSessionProvider provider = new(generator);
        Assert.Empty(provider.PreflightRequest(new(
            plan,
            Ltx2ArchitectureModule.ArchitectureId)));
        // The positive control for the line above. Video2VideoCreativity is set, so the fallback's
        // warning is live in this request — an empty list under a specialized root owner is the
        // guard doing its job, not a dead emitter.
        Assert.Contains(
            provider.PreflightRequest(new(plan, HostVideoArchitectureModule.ArchitectureId)),
            diagnostic => diagnostic.Code == "host-video.creativity.ignored");
        // Compile-time codes only: PreflightRequest's output never reaches the plan, so this pins
        // that the later generic clip also drew no host-video.stage-control.* error.
        Assert.DoesNotContain(
            plan.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith("host-video.", StringComparison.Ordinal));

        WorkflowGenerator.ImageToVideoGenInfo core = new()
        {
            Generator = generator,
            ContextID = T2IParamInput.SectionID_Video,
            VideoSwapModel = hostModel,
            VideoSwapPercent = 0.2,
            VideoEndFrame = new Image([0x01], MediaType.ImagePng),
            StartStep = 4,
        };
        HostVideoCorePassIsolation.Isolate(core);

        Assert.Same(hostModel, core.VideoSwapModel);
        Assert.Equal(0.2, core.VideoSwapPercent);
        Assert.NotNull(core.VideoEndFrame);
        Assert.Equal(4, core.StartStep);
        Assert.False(core.HasMatchedModelData);
    }

    /// <summary>
    /// The fallback's request-global warnings reach the user only through the lifecycle:
    /// <c>PreflightRequest</c> runs inside <c>PrepareRequest()</c> and lands in
    /// <see cref="VideoExecutionPlanContext.PreflightDiagnostics"/> — never in the compiled plan's
    /// own diagnostics, which are fixed before any architecture is asked.
    /// </summary>
    [Fact]
    public void A_host_video_root_warns_at_preflight_that_request_global_settings_are_ignored()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));
        input.Set(T2IParamTypes.Video2VideoCreativity, 0.25);
        input.Set(T2IParamTypes.VideoEndFrame, new Image([0x01], MediaType.ImagePng));
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/",
            Workflow = [],
        };
        VideoExecutionPlanContext request = generator.RequireVideoExecutionPlanContext();
        Assert.DoesNotContain(
            request.Plan.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith("host-video.", StringComparison.Ordinal));

        request.PrepareRequest();

        Assert.Equal(
            ["host-video.creativity.ignored", "host-video.end-frame.ignored"],
            request.PreflightDiagnostics
                .Select(diagnostic => diagnostic.Code)
                .Order());
        // Warnings, not errors — PrepareRequest would have thrown on a blocking one.
        Assert.All(
            request.PreflightDiagnostics,
            diagnostic => Assert.Equal(PlanDiagnosticSeverity.Warning, diagnostic.Severity));
        Assert.Equal(VideoExecutionState.Prepared, request.State);
    }

    [Fact]
    public void Core_isolation_failure_is_sticky_after_partial_mutation()
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = HostModel(
            T2IModelClassSorter.CompatHunyuanVideo1_5,
            "hunyuan-video-1_5");
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/",
            Workflow = [],
        };
        VideoExecutionPlanContext request =
            generator.RequireVideoExecutionPlanContext();
        request.PrepareRequest();
        WorkflowGenerator.ImageToVideoGenInfo core = new()
        {
            Generator = generator,
            ContextID = T2IParamInput.SectionID_Video,
            VideoSwapModel = models.VideoModel,
            VideoSwapPercent = 0.2,
            VideoEndFrame = new Image([0x01], MediaType.ImagePng),
            StartStep = 4,
        };

        InvalidOperationException first = Assert.Throws<InvalidOperationException>(
            () => HostVideoCorePassIsolation.Isolate(core));
        core.VideoSwapModel = models.VideoModel;
        InvalidOperationException repeated = Assert.Throws<InvalidOperationException>(
            () => HostVideoCorePassIsolation.Isolate(core));

        Assert.Contains("no live base model", first.Message);
        Assert.Same(first, repeated);
        Assert.Equal(VideoExecutionState.Failed, request.State);
        Assert.Same(models.VideoModel, core.VideoSwapModel);
    }

    [Theory]
    [InlineData("nvidia-cosmos-predict2-t2i-2b", "nvidia-cosmos-predict2-t2i-2b")]
    [InlineData("nvidia-cosmos-predict2-t2i-14b", "nvidia-cosmos-predict2-t2i-14b")]
    public void Cosmos_Predict2_text_to_image_flags_are_rejected_before_graph_mutation(
        string modelClassId,
        string compatibilityClassId)
    {
        using SwarmUiTestContext context = new();
        T2IModelCompatClass compatibility = new()
        {
            ID = compatibilityClassId,
            IsText2Video = true,
            IsImage2Video = true,
        };
        TestModelBundle models = HostModel(compatibility, modelClassId);
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));

        AssertRejectedBeforeMutation(
            input,
            "does not resolve to a registered video architecture");
    }

    private static TestModelBundle HostModel(
        T2IModelCompatClass compatibility,
        string modelClassId)
    {
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        models.VideoModel.ModelClass = models.VideoModel.ModelClass with
        {
            ID = modelClassId,
            Name = modelClassId,
            CompatClass = compatibility,
            StandardWidth = 960,
            StandardHeight = 960,
        };
        return models;
    }

    private static T2IModel AddHostModel(
        T2IModel existing,
        T2IModelCompatClass compatibility,
        string modelClassId,
        string name)
    {
        T2IModelHandler handler = Program.T2IModelSets["Stable-Diffusion"];
        T2IModel model = new(handler, TestStubModel.Folder(handler), TestStubModel.File(handler, name), name)
        {
            ModelClass = existing.ModelClass with
            {
                ID = modelClassId,
                Name = modelClassId,
                CompatClass = compatibility,
            },
        };
        handler.Models[model.Name] = model;
        return model;
    }

    private static void AssertRejectedBeforeMutation(
        T2IParamInput input,
        string expectedReason)
    {
        WorkflowGenerator captured = null;
        JObject before = null;
        WorkflowGenerator.WorkflowGenStep snapshot = new(g =>
        {
            captured = g;
            before = (JObject)g.Workflow.DeepClone();
        }, Constants.WorkflowStepPriority.PreflightRequest - 0.1);
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(() =>
            WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot, WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps())));

        Assert.Contains(expectedReason, error.Message);
        Assert.True(JToken.DeepEquals(before, captured.Workflow));
    }
}
