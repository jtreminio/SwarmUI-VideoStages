using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class RootVideoStageResizerContractTests
{
    [Fact]
    public void Alternate_handlers_reject_invalid_plan_before_mutating_host_state()
    {
        using SwarmUiTestContext _ = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndVideoModels();
        T2IParamInput input = BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(MakeStage(models.VideoModel.Name, "Generated")));
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

        SwarmUserErrorException preError = Assert.Throws<SwarmUserErrorException>(() =>
            RootVideoStageResizer.ApplyRootResolutionBeforeImageToVideo(genInfo));
        SwarmUserErrorException postError = Assert.Throws<SwarmUserErrorException>(() =>
            RootVideoStageResizer.ApplyRootLatentResolutionAfterImageToVideo(genInfo));

        Assert.Contains(
            "does not resolve to a registered video architecture",
            preError.Message);
        Assert.Equal(preError.Message, postError.Message);
        Assert.Equal(640, genInfo.Width);
        Assert.Equal(360, genInfo.Height);
        Assert.Equal(320, generator.CurrentMedia.Width);
        Assert.Equal(180, generator.CurrentMedia.Height);
        Assert.Equal(12, generator.CurrentMedia.Frames);
        Assert.True(JToken.DeepEquals(workflowBefore, generator.Workflow));
        Assert.True(JToken.DeepEquals(mediaPathBefore, generator.CurrentMedia.Path));
    }
}
