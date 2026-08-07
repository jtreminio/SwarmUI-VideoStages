namespace VideoStages.Planning;

internal enum PlanDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// <see cref="Code"/> is the stable identifier tests match on; <see cref="Message"/> is what the
/// user reads. A diagnostic sets whichever identity fields it knows.
/// </summary>
internal sealed record PlanDiagnostic(
    PlanDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? ClipId = null,
    int? StageId = null,
    int? RawStageIndex = null,
    string TrackId = null);
