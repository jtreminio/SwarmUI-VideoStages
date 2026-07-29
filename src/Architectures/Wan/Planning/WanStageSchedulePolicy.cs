namespace VideoStages.Architectures.Wan.Planning;

/// <summary>
/// The one arithmetic policy for mapping authored Wan regeneration control and the host swap split
/// onto integer sampler steps. Compilation, request preflight, runtime, and tests all consume these
/// exact floor/round semantics.
/// </summary>
internal static class WanStageSchedulePolicy
{
    internal static int StartStep(int steps, double control) =>
        (int)Math.Floor(steps * (1 - control));

    internal static int HostHighEndStep(int steps, double swapPercent) =>
        (int)Math.Round(steps * (1 - swapPercent));

    internal static bool IsQuantizedZeroPartial(int steps, double control) =>
        control < 1 && StartStep(steps, control) == 0;

    internal static bool HasSwapHighNoiseWindow(
        int steps,
        double control,
        double swapPercent) =>
        StartStep(steps, control) < HostHighEndStep(steps, swapPercent);
}
