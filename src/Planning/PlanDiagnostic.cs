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

/// <summary>The plan diagnostic codes the frontend also names. The rest stay literals at the site
/// that emits them: nothing outside the backend reads those, and a projected constant with no
/// reader is a liability.</summary>
internal static class PlanDiagnosticCodes
{
    /// <summary>The frontend refuses to author what this reports after the fact.</summary>
    internal const string RetakeSourceRequired = "retake-source-required";

    /// <summary>
    /// Renders the checked-in TypeScript projection. A backend test compares this byte for byte
    /// with <c>frontend/generatedPlanDiagnostics.ts</c>.
    /// </summary>
    internal static string RenderGeneratedTypeScript()
    {
        StringBuilder result = new();
        void Line(string value = "") => result.Append(value).Append('\n');

        Line("// Generated from PlanDiagnostic.cs. Do not edit by hand.");
        Line();
        Line($"export const PLAN_DIAGNOSTIC_RETAKE_SOURCE_REQUIRED = \"{RetakeSourceRequired}\";");

        return result.ToString();
    }
}
