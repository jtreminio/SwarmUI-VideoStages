using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Authoring;

namespace VideoStages;

public static class Runner
{
    /// <summary>
    /// Wraps a phase as a workflow step. The gate is what keeps a registered step inert on a
    /// request this extension is not driving; nothing else supplies it.
    /// </summary>
    internal static Action<WorkflowGenerator> Phase(Action<VideoExecutionPlanContext> phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        return g =>
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

    public static void PreflightRequest(WorkflowGenerator g)
    {
        if (!DocumentJson.IsActive(g.UserInput))
        {
            return;
        }

        g.GetVideoExecutionPlanContext()?.PrepareRequest();
    }
}
