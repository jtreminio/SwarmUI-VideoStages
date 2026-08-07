using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;
using static VideoStages.Tests.Fixtures;

namespace VideoStages.Tests;

/// <summary>
/// What the frame interpolator does that a real generated graph cannot show: refuse an invalid
/// request before any phase mutates the workflow. The graph-level contracts live in
/// <see cref="FrameInterpolationContractTests"/>.
/// </summary>
[Collection("VideoStagesTests")]
public sealed class TimelineFrameInterpolatorTests
{
    private sealed class PreflightSnapshot
    {
        internal WorkflowGenerator Generator { get; private set; }
        internal JObject Workflow { get; private set; }
        internal WGNodeData Media { get; private set; }
        internal Dictionary<string, string> NodeHelpers { get; private set; }

        internal WorkflowGenerator.WorkflowGenStep Step() => new(g =>
        {
            Generator = g;
            Workflow = (JObject)g.Workflow.DeepClone();
            Media = g.CurrentMedia;
            NodeHelpers = new(g.NodeHelpers);
        }, Constants.WorkflowStepPriority.PreflightRequest - 0.5);

        internal void AssertUnchanged()
        {
            Assert.True(JToken.DeepEquals(Workflow, Generator.Workflow));
            Assert.Same(Media, Generator.CurrentMedia);
            Assert.Equal(NodeHelpers, Generator.NodeHelpers);
        }
    }

    [Theory]
    [InlineData("missing-method", null, 2, "explicitly selected")]
    [InlineData("unknown-method", "Unknown-VFI", 2, "is not supported")]
    [InlineData("invalid-low", "RIFE", 0, "between 2 and 10")]
    [InlineData("invalid-high", "RIFE", 11, "between 2 and 10")]
    public void Invalid_active_configuration_is_refused_before_mutation(
        string _,
        string method,
        int multiplier,
        string expected)
    {
        using SwarmUiTestContext context = new();
        TestModelBundle models = TestModelFactory.CreateBaseAndWan22ImageToVideoModels();
        T2IParamInput input = WanInput(models);
        input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMultiplier, multiplier);
        if (method is not null)
        {
            input.Set(ComfyUIBackendExtension.VideoFrameInterpolationMethod, method);
        }

        AssertPreflightFailure(input, ["variation_seed", "frameinterps"], expected);
    }

    private static void AssertPreflightFailure(
        T2IParamInput input,
        IEnumerable<string> features,
        string expected)
    {
        PreflightSnapshot snapshot = new();
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => WorkflowTestHarness.GenerateWithStepsAndState(
                input,
                WorkflowTestHarness.Template_BaseOnlyImage()
                    .Concat([snapshot.Step(), WorkflowTestHarness.CoreImageToVideoStep()])
                    .Concat(WorkflowTestHarness.VideoStagesSteps()),
                features));
        Assert.Contains(expected, error.Message);
        snapshot.AssertUnchanged();
    }

    private static T2IParamInput WanInput(TestModelBundle models) =>
        BuildNativeInput(
            models.BaseModel,
            models.VideoModel,
            JsonSingleClipStages(
                MakeStage(models.VideoModel.Name, "Generated", steps: 8)));
}
