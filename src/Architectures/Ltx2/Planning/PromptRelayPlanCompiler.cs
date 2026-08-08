using System.Collections.Immutable;
using VideoStages.Authoring;

namespace VideoStages.Architectures.Ltx2.Planning;

/// <summary>Compiles authored prompt windows into a clip-level relay instruction.</summary>
internal static class PromptRelayPlanCompiler
{
    internal static PromptRelayPlan Compile(ClipSpec clip, int framesPerSecond)
    {
        ImmutableArray<PromptWindowPlan> windows = (clip.PromptWindows ?? [])
            .Where(window => window is not null
                && !string.IsNullOrWhiteSpace(window.Prompt)
                && window.Duration > 0)
            .OrderBy(window => window.Start)
            .Select(window => new PromptWindowPlan(
                window.Prompt.Trim(),
                window.Start,
                window.Duration,
                window.Start + window.Duration))
            .ToImmutableArray();
        if (windows.IsEmpty)
        {
            return new(PromptRelayMode.None, windows, []);
        }
        if (clip.Frames is not int frames || framesPerSecond <= 0)
        {
            return new(PromptRelayMode.RequiresRuntimeLength, windows, []);
        }

        ImmutableArray<PromptRelaySegmentPlan> segments =
            Tile(windows, frames / (double)framesPerSecond);
        return new(ModeFor(segments), windows, segments);
    }

    internal static PromptRelayMode ModeFor(
        IReadOnlyList<PromptRelaySegmentPlan> segments) => segments.Count switch
        {
            0 => PromptRelayMode.None,
            1 when !string.IsNullOrWhiteSpace(segments[0].Prompt) =>
                PromptRelayMode.SinglePromptOverride,
            >= 2 => PromptRelayMode.Relay,
            _ => PromptRelayMode.None,
        };

    /// <summary>
    /// Tiles windows that <see cref="Compile"/> has already filtered and start-ordered, filling
    /// every gap and the tail with an empty-prompt segment — that filler is what turns a single
    /// authored window into the multi-segment relay <see cref="ModeFor"/> looks for.
    /// </summary>
    internal static ImmutableArray<PromptRelaySegmentPlan> Tile(
        IEnumerable<PromptWindowPlan> windows,
        double clipSeconds)
    {
        PromptWindowPlan[] activeWindows = [.. windows ?? []];
        if (activeWindows.Length == 0)
        {
            return [];
        }
        const double epsilon = 1e-4;
        double total = Math.Max(0, clipSeconds);
        double cursor = 0;
        ImmutableArray<PromptRelaySegmentPlan>.Builder segments =
            ImmutableArray.CreateBuilder<PromptRelaySegmentPlan>();
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
