using System.Collections.Immutable;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Verifies the custom nodes that planned LTX stages will emit before graph mutation begins.
/// </summary>
internal static class Ltx2RequestPreflight
{
    internal static IReadOnlyList<PlanDiagnostic> Resolve(
        IReadOnlyCollection<string> features,
        VideoExecutionPlan plan)
    {
        List<PlanDiagnostic> diagnostics = [];
        bool nodesAvailable = Ltx2HostIntegration.IsAvailable(features);
        bool reportedMissingNodes = false;
        foreach (ClipPlan clip in plan.Clips.Where(
            clip => clip.Architecture.Id == Ltx2ArchitectureModule.ArchitectureId))
        {
            foreach (StagePlan stage in clip.Stages)
            {
                ImmutableArray<IcLoraPlan> icLoras = stage.RequireLtx2Payload().IcLoras;
                if (icLoras.IsDefaultOrEmpty)
                {
                    continue;
                }
                if (!nodesAvailable && !reportedMissingNodes)
                {
                    reportedMissingNodes = true;
                    diagnostics.Add(new(
                        PlanDiagnosticSeverity.Error,
                        "ltx2.iclora.nodes-missing",
                        "IC-LoRAs require the ComfyUI-LTXVideo custom nodes. "
                        + $"Install {Ltx2HostIntegration.NodeUrl} "
                        + "or use SwarmUI's LTXVideo feature installer.",
                        clip.ClipId,
                        stage.StageId));
                }
            }
        }
        return diagnostics;
    }
}
