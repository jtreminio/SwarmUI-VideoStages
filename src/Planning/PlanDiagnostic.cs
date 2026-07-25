namespace VideoStages.Planning;

internal enum PlanDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The one diagnostic record every VideoStages planner emits. <see cref="Code"/> is intentionally
/// machine-friendly for plan snapshots and tests; <see cref="Message"/> is what the user reads.
/// The identity fields are all optional: a diagnostic names whichever of clip, stage, audio track,
/// or track span it actually knows about.
/// </summary>
internal sealed record PlanDiagnostic(
    PlanDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? ClipId = null,
    int? StageId = null,
    int? RawStageIndex = null,
    string TrackId = null,
    int? SpanIndex = null);
