namespace VideoStages.LTX2;

internal static class PromptWindowTiler
{
    public static List<(string Prompt, double Seconds)> TilePromptWindows(
        IReadOnlyList<PromptWindowSpec> windows,
        double clipSeconds)
    {
        const double epsilon = 1e-4;
        double total = clipSeconds > 0 ? clipSeconds : 0;

        List<PromptWindowSpec> active = [.. windows
            .Where(w => !string.IsNullOrWhiteSpace(w.Prompt) && w.Duration > 0)
            .OrderBy(w => w.Start)];
        if (active.Count == 0)
        {
            return [];
        }

        List<(string Prompt, double Seconds)> tiled = [];
        double cursor = 0;
        foreach (PromptWindowSpec window in active)
        {
            double start = Math.Clamp(window.Start, 0, total);
            double end = Math.Clamp(window.Start + window.Duration, start, total);
            if (start > cursor + epsilon)
            {
                tiled.Add(("", start - cursor));
                cursor = start;
            }
            if (end > cursor + epsilon)
            {
                tiled.Add((window.Prompt.Trim(), end - cursor));
                cursor = end;
            }
        }
        if (total - cursor > epsilon)
        {
            tiled.Add(("", total - cursor));
        }
        return tiled;
    }
}
