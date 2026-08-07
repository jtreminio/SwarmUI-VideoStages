using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;

namespace VideoStages;

internal static class Runner
{
    // The gate is what keeps a registered step inert on a request this extension is not driving.
    internal static Action<WorkflowGenerator> Phase(Action<VideoExecutionPlanContext> phase) =>
        g =>
        {
            if (!DocumentJson.IsActive(g.UserInput))
            {
                return;
            }
            if (g.GetVideoExecutionPlanContext() is VideoExecutionPlanContext context)
            {
                phase(context);
            }
        };
}
