using System.Collections.Immutable;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2;

/// <summary>
/// Everything LTX needs before the first workflow phase mutates anything: the custom nodes its
/// planned stages will emit, and the IC-LoRA weights those stages name. Both are answerable from the
/// compiled plan plus the backend feature set, so no graph state is required or touched.
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
                foreach (IcLoraPlan icLora in icLoras)
                {
                    if (IcLoraModelResolver.DescribeUnresolvable(icLora) is string unresolvable)
                    {
                        diagnostics.Add(new(
                            PlanDiagnosticSeverity.Error,
                            "ltx2.iclora.weights-unresolved",
                            unresolvable,
                            clip.ClipId,
                            stage.StageId));
                    }
                }
            }
        }
        return diagnostics;
    }
}
