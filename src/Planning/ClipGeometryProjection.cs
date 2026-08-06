namespace VideoStages.Planning;

/// <summary>
/// Projects final clip dimensions for planning diagnostics. Runtime geometry conforming remains
/// the fallback when dimensions cannot be projected.
/// </summary>
internal static class ClipGeometryProjection
{
    internal static IReadOnlyList<PlanDiagnostic> Validate(
        IReadOnlyList<ClipPlan> clips,
        int rootWidth,
        int rootHeight)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clips.Count < 2 || rootWidth <= 0 || rootHeight <= 0)
        {
            return [];
        }
        Dictionary<int, (int Width, int Height)> projected = [];
        foreach (ClipPlan clip in clips)
        {
            if (clip.ArchitecturePayload is null)
            {
                continue;
            }
            projected[clip.ClipId] = clip.ArchitecturePayload.ProjectFinalDimensions(
                clip.Stages,
                clip.InitVideo?.TargetWidth ?? rootWidth,
                clip.InitVideo?.TargetHeight ?? rootHeight);
        }
        if (projected.Count < 2)
        {
            return [];
        }

        int targetWidth = projected.Values.Min(size => size.Width);
        int targetHeight = projected.Values.Min(size => size.Height);
        List<PlanDiagnostic> diagnostics = [];
        foreach ((int clipId, (int width, int height)) in projected.OrderBy(entry => entry.Key))
        {
            if (width == targetWidth && height == targetHeight)
            {
                continue;
            }
            diagnostics.Add(width * targetHeight != height * targetWidth
                ? new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "clip-aspect-mismatch",
                    $"clip {clipId} is planned to finish at {width}x{height}, whose aspect ratio "
                    + $"differs from the timeline's {targetWidth}x{targetHeight}; conforming it "
                    + "would distort the image",
                    clipId)
                : new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "clip-geometry-will-conform",
                    $"clip {clipId} is planned to finish at {width}x{height} and will be conformed "
                    + $"to {targetWidth}x{targetHeight} before the timeline is merged",
                    clipId));
        }
        return diagnostics;
    }
}
