using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;

namespace VideoStages;

/// <summary>Host workflow facts used before an architecture runtime is selected.</summary>
internal static class RootHostWorkflowFacts
{
    internal static WorkflowGenerator.WorkflowGenStep
        ResolveCoreImageToVideoStep(
            IEnumerable<WorkflowGenerator.WorkflowGenStep> steps,
            out string diagnostic)
    {
        WorkflowGenerator.WorkflowGenStep[] matches =
        [
            .. (steps ?? []).Where(
                step => step.Priority
                    == Constants.WorkflowStepPriority.CoreImageToVideo)
        ];
        if (matches.Length == 1)
        {
            diagnostic = null;
            return matches[0];
        }
        diagnostic =
            "VideoStages expected exactly one SwarmUI core image-to-video "
            + $"workflow step at priority {Constants.WorkflowStepPriority.CoreImageToVideo}, "
            + $"but found {matches.Length}. Core video handoff is disabled; this "
            + "SwarmUI version's workflow-step contract may have changed.";
        return null;
    }

    internal static bool IsTextToVideoRootWorkflow(WorkflowGenerator generator)
    {
        if (generator.UserInput.TryGet(
                T2IParamTypes.VideoModel,
                out T2IModel existingVideoModel)
            && existingVideoModel is not null)
        {
            return false;
        }
        return generator.UserInput.TryGet(
                T2IParamTypes.Model,
                out T2IModel textToVideoModel)
            && textToVideoModel?.ModelClass?.CompatClass?.IsText2Video == true;
    }

    internal static bool CanInterceptHostCore(
        WorkflowGenerator generator,
        VideoStagesSpec spec)
    {
        if (VideoStagesExtension.CoreImageToVideoStep is null
            || !spec.Clips.Any(clip => clip.Stages.Count > 0))
        {
            return false;
        }
        bool hasNativeVideoModel = generator.UserInput.TryGet(
            T2IParamTypes.VideoModel,
            out T2IModel _);
        return !hasNativeVideoModel
            || WorkflowGenerator.Steps.Contains(VideoStagesExtension.CoreImageToVideoStep);
    }
}
