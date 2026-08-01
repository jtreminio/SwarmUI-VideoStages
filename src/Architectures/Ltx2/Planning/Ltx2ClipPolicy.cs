using VideoStages.Architectures.Abstractions;
using VideoStages.Planning;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>LTX clip preconditions the per-clip compiler cannot see on its own.</summary>
internal static class Ltx2ClipPolicy
{
    /// <summary>
    /// Audio reuse needs generate, capture, then reuse. Falling short is not an authoring
    /// decision to gate or report, so this stays a compile-time eligibility fact.
    /// </summary>
    internal const int AudioReuseMinimumActiveStages = 3;

    internal const string RetakeRequiresSourceCode = "retake-source-required";

    internal static IReadOnlyList<ArchitectureEntryMode> RetakeEntryModes { get; } =
        [ArchitectureEntryMode.InitVideo];

    internal static IReadOnlyList<PlanDiagnostic> Validate(IReadOnlyList<ClipPlan> clips)
    {
        List<PlanDiagnostic> diagnostics = [];
        foreach (ClipPlan clip in clips)
        {
            foreach (StagePlan stage in clip.Stages)
            {
                if (stage.ArchitecturePayload is Ltx2StagePayload { Retake: not null }
                    && !RetakeEntryModes.Contains(clip.EntryMode))
                {
                    diagnostics.Add(new(
                        PlanDiagnosticSeverity.Error,
                        RetakeRequiresSourceCode,
                        "Retake requires an init-video clip.",
                        clip.ClipId,
                        stage.StageId));
                }
            }
        }

        return diagnostics.AsReadOnly();
    }
}
