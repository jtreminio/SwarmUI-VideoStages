using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Architectures;
using VideoStages.Authoring;

namespace VideoStages;

internal static class WorkflowPhase
{
    /// <summary>Every phase but the first and last is one host call under the prepared guard.</summary>
    internal static Action<WorkflowGenerator> OnHost(
        Action<VideoArchitectureExecutionHost> phase) =>
        Guarded(context => context.ExecutePrepared(phase));

    // The gate is what keeps a registered step inert on a request this extension is not driving.
    internal static Action<WorkflowGenerator> Guarded(Action<VideoExecutionPlanContext> phase) =>
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
