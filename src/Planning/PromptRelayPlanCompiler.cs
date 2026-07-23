using System.Collections.Immutable;

namespace VideoStages.Planning;

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
            PromptRelayPlanResolver.Tile(windows, frames / (double)framesPerSecond);
        PromptRelayMode mode = segments.Length switch
        {
            0 => PromptRelayMode.None,
            1 when !string.IsNullOrWhiteSpace(segments[0].Prompt) =>
                PromptRelayMode.SinglePromptOverride,
            >= 2 => PromptRelayMode.Relay,
            _ => PromptRelayMode.None,
        };
        return new(mode, windows, segments);
    }
}
