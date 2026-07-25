using System.Collections.Immutable;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>
/// Resolves authored prompt windows into a complete, ordered clip timeline. This is shared by
/// compile-time planning and the one runtime fallback where a clip's frame count is not known yet.
/// </summary>
internal static class PromptRelayPlanResolver
{
    internal static ImmutableArray<PromptRelaySegmentPlan> Tile(
        IEnumerable<PromptWindowPlan> windows,
        double clipSeconds)
    {
        const double epsilon = 1e-4;
        double total = Math.Max(0, clipSeconds);
        double cursor = 0;
        ImmutableArray<PromptRelaySegmentPlan>.Builder segments =
            ImmutableArray.CreateBuilder<PromptRelaySegmentPlan>();

        PromptWindowPlan[] activeWindows = (windows ?? [])
            .Where(window => window is not null
                && !string.IsNullOrWhiteSpace(window.Prompt)
                && window.DurationSeconds > 0)
            .OrderBy(window => window.StartSeconds)
            .ToArray();
        if (activeWindows.Length == 0)
        {
            return [];
        }

        foreach (PromptWindowPlan window in activeWindows)
        {
            double start = Math.Clamp(window.StartSeconds, 0, total);
            double end = Math.Clamp(window.EndSeconds, start, total);
            if (start > cursor + epsilon)
            {
                segments.Add(new("", start - cursor));
                cursor = start;
            }
            if (end > cursor + epsilon)
            {
                segments.Add(new(window.Prompt, end - cursor));
                cursor = end;
            }
        }
        if (total - cursor > epsilon)
        {
            segments.Add(new("", total - cursor));
        }
        return segments.ToImmutable();
    }
}
