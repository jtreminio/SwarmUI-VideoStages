using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;

namespace VideoStages.Tests;

public class RootHostWorkflowFactsTests
{
    [Fact]
    public void Missing_core_image_to_video_step_disables_handoff_with_a_diagnostic()
    {
        WorkflowGenerator.WorkflowGenStep resolved =
            RootHostWorkflowFacts.ResolveCoreImageToVideoStep(
                [],
                out string diagnostic);

        Assert.Null(resolved);
        Assert.Contains(
            $"{Constants.WorkflowStepPriority.CoreImageToVideo}",
            diagnostic);
        Assert.Contains("handoff is disabled", diagnostic);
    }

    [Fact]
    public void Unique_core_image_to_video_step_is_returned_without_a_diagnostic()
    {
        WorkflowGenerator.WorkflowGenStep expected = Step(
            Constants.WorkflowStepPriority.CoreImageToVideo);

        WorkflowGenerator.WorkflowGenStep resolved =
            RootHostWorkflowFacts.ResolveCoreImageToVideoStep(
                [Step(10), expected, Step(12)],
                out string diagnostic);

        Assert.Same(expected, resolved);
        Assert.Null(diagnostic);
    }

    [Fact]
    public void Ambiguous_core_image_to_video_steps_disable_handoff()
    {
        WorkflowGenerator.WorkflowGenStep resolved =
            RootHostWorkflowFacts.ResolveCoreImageToVideoStep(
                [
                    Step(Constants.WorkflowStepPriority.CoreImageToVideo),
                    Step(Constants.WorkflowStepPriority.CoreImageToVideo),
                ],
                out string diagnostic);

        Assert.Null(resolved);
        Assert.Contains("found 2", diagnostic);
        Assert.Contains("handoff is disabled", diagnostic);
    }

    private static WorkflowGenerator.WorkflowGenStep Step(double priority) =>
        new(_ => { }, priority);
}
