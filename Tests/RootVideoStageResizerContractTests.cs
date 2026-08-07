using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;
using VideoStages.Architectures.Ltx2.Runtime;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class RootVideoStageResizerContractTests
{
    /// <summary>
    /// The plan must be LTX-owned, otherwise the handlers bail on the architecture-ownership check
    /// and never reach the failed-request guard this test exists for; a second stage on a model that
    /// resolves to no architecture is what makes preflight reject an otherwise LTX-owned plan.
    /// </summary>
    [Fact]
    public void Central_preflight_rejects_invalid_plan_and_ltx_handlers_refuse_to_mutate()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndLtxv2VideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated"),
                MakeStage("not-an-ltx-model", "PreviousStage")));
        WorkflowGenerator generator = new()
        {
            UserInput = input,
            Features = [],
            ModelFolderFormat = "/",
            Workflow = new JObject
            {
                ["11"] = new JObject
                {
                    ["class_type"] = "LoadImage",
                    ["inputs"] = new JObject(),
                },
            },
        };
        generator.CurrentMedia = new WGNodeData(
            new JArray("11", 0),
            generator,
            WGNodeData.DT_VIDEO,
            null)
        {
            Width = 320,
            Height = 180,
            Frames = 12,
        };
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = generator,
            ContextID = T2IParamInput.SectionID_Video,
            Width = 640,
            Height = 360,
        };
        JObject workflowBefore = (JObject)generator.Workflow.DeepClone();
        JArray mediaPathBefore = (JArray)generator.CurrentMedia.Path.DeepClone();

        VideoExecutionPlanContext request = Assert.IsType<VideoExecutionPlanContext>(
            generator.GetVideoExecutionPlanContext());
        Assert.Equal(Ltx2ArchitectureModule.ArchitectureId, request.RootOwnerArchitectureId);
        SwarmUserErrorException preflightError = Assert.Throws<SwarmUserErrorException>(() =>
            Runner.PreflightRequest(generator));
        // Applicable handlers must replay the memoized preflight failure, not silently no-op.
        SwarmUserErrorException beforeError = Assert.Throws<SwarmUserErrorException>(() =>
            RootVideoStageResizer.ApplyRootResolutionBeforeImageToVideo(genInfo));
        SwarmUserErrorException afterError = Assert.Throws<SwarmUserErrorException>(() =>
            RootVideoStageResizer.ApplyRootLatentResolutionAfterImageToVideo(genInfo));

        Assert.Contains(
            "does not resolve to a registered video architecture",
            preflightError.Message);
        Assert.Same(preflightError, beforeError);
        Assert.Same(preflightError, afterError);
        Assert.Equal(VideoExecutionState.Failed, request.State);
        Assert.Equal(640, genInfo.Width);
        Assert.Equal(360, genInfo.Height);
        Assert.Equal(320, generator.CurrentMedia.Width);
        Assert.Equal(180, generator.CurrentMedia.Height);
        Assert.Equal(12, generator.CurrentMedia.Frames);
        Assert.True(JToken.DeepEquals(workflowBefore, generator.Workflow));
        Assert.True(JToken.DeepEquals(mediaPathBefore, generator.CurrentMedia.Path));
    }
}
