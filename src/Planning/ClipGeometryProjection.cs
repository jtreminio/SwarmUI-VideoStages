using VideoStages.Architectures.Abstractions;

namespace VideoStages.Planning;

/// <summary>
/// Projects, before anything runs, the dimensions each clip's authored stage chain will finish at,
/// so the timeline can warn about conforming at plan time instead of only from the running graph.
/// The projection is advisory: runtime conforming stays the backstop for whatever it cannot see.
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
                // An uncompiled clip would make the timeline minimum a guess; the plan already
                // carries the error that blocked its compilation.
                return [];
            }
            projected[clip.ClipId] = clip.ArchitecturePayload.ProjectFinalDimensions(
                clip.Stages,
                clip.InitVideo?.TargetWidth ?? rootWidth,
                clip.InitVideo?.TargetHeight ?? rootHeight);
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
                    PlanDiagnosticSeverity.Error,
                    "clip-aspect-mismatch",
                    $"clip {clipId} is planned to finish at {width}x{height}, whose aspect ratio "
                    + $"differs from the timeline's {targetWidth}x{targetHeight}; conforming it "
                    + "would distort the image",
                    clipId)
                : new PlanDiagnostic(
                    PlanDiagnosticSeverity.Warning,
                    "clip-geometry-will-conform",
                    $"clip {clipId} is planned to finish at {width}x{height} and will be conformed "
                    + $"to {targetWidth}x{targetHeight} before the timeline is assembled",
                    clipId));
        }
        return diagnostics;
    }
}
