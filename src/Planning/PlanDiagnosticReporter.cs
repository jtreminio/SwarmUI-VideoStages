using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoStages.Planning;

/// <summary>
/// The one place that decides what a compiled plan's diagnostics do to the user: errors block the
/// generation, warnings reach the host's warning channel, and info stays in the debug log.
/// </summary>
internal static class PlanDiagnosticReporter
{
    internal static IReadOnlyList<PlanDiagnostic> Errors(
        IEnumerable<PlanDiagnostic> diagnostics) => [
            .. (diagnostics ?? []).Where(
                diagnostic => diagnostic.Severity == PlanDiagnosticSeverity.Error)
        ];

    /// <summary>
    /// Fails the generation closed when any diagnostic is blocking. <paramref name="context"/> names
    /// the stage of the request that produced them.
    /// </summary>
    internal static void ThrowIfBlocking(
        IEnumerable<PlanDiagnostic> diagnostics,
        string context)
    {
        IReadOnlyList<PlanDiagnostic> errors = Errors(diagnostics);
        if (errors.Count == 0)
        {
            return;
        }
        throw new SwarmUserErrorException(
            $"{context}: {string.Join("; ", errors.Select(error => error.Message))}");
    }

    private static string Format(PlanDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        List<string> identity = [];
        if (diagnostic.ClipId is int clipId)
        {
            identity.Add($"clip {clipId}");
        }
        if (diagnostic.StageId is int stageId)
        {
            identity.Add($"stage {stageId}");
        }
        if (!string.IsNullOrEmpty(diagnostic.TrackId))
        {
            identity.Add($"audio track '{diagnostic.TrackId}'");
        }
        if (diagnostic.SpanIndex is int spanIndex)
        {
            identity.Add($"span {spanIndex}");
        }
        string suffix = identity.Count == 0 ? "" : $" ({string.Join(", ", identity)})";
        return $"VideoStages: {diagnostic.Message}{suffix}";
    }

    /// <summary>
    /// Emits every non-blocking diagnostic exactly once. Duplicate lines are collapsed because the
    /// same planner rule can legitimately fire for several clips with identical wording.
    /// </summary>
    internal static void Report(
        IEnumerable<PlanDiagnostic> diagnostics,
        Action<string> warn = null,
        Action<string> info = null,
        Action<string> error = null)
    {
        warn ??= Logs.Warning;
        info ??= Logs.Debug;
        error ??= Logs.Error;
        HashSet<string> reported = new(StringComparer.Ordinal);
        foreach (PlanDiagnostic diagnostic in diagnostics ?? [])
        {
            if (diagnostic is null)
            {
                continue;
            }
            string line = Format(diagnostic);
            if (!reported.Add($"{diagnostic.Severity}\0{line}"))
            {
                continue;
            }
            switch (diagnostic.Severity)
            {
                case PlanDiagnosticSeverity.Error:
                    error(line);
                    break;
                case PlanDiagnosticSeverity.Warning:
                    warn(line);
                    break;
                default:
                    info(line);
                    break;
            }
        }
    }

    /// <summary>
    /// Emits diagnostics through the normal logs and persists warnings in SwarmUI's established
    /// browser-visible warning metadata. The host serializes <c>parser_warnings</c> with completed
    /// output metadata and gives that field warning styling in the browser.
    /// </summary>
    internal static void ReportToRequest(
        IEnumerable<PlanDiagnostic> diagnostics,
        T2IParamInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Report(
            diagnostics,
            warning => RequestWarnings.Track(input, warning));
    }
}
