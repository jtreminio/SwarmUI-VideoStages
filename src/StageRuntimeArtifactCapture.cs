using ComfyTyped.Core;
using SwarmUI.Builtin_ComfyUIBackend;
using VideoStages.Execution;
using VideoStages.Planning;

namespace VideoStages;

/// <summary>Captures and validates a stage's terminal runtime artifact.</summary>
internal sealed class StageRuntimeArtifactCapture(WorkflowGenerator g)
{
    public RuntimeArtifact Capture(StagePlan stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        using WorkflowBridge bridge = WorkflowBridge.Create(g.Workflow);
        RuntimeArtifact output = RuntimeArtifact.Capture(g, bridge);
        if (!output.HasMedia)
        {
            throw VideoStagesInvariant.Failure(
                $"VideoStages: stage {stage.StageId} did not produce a video artifact.");
        }
        return output;
    }
}
