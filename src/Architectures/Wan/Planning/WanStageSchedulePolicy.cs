namespace VideoStages.Architectures.Wan.Planning;

/// <summary>
/// The one arithmetic policy for mapping authored Wan regeneration control onto integer sampler
/// steps. Compilation, runtime, and tests all consume these exact floor semantics.
/// </summary>
internal static class WanStageSchedulePolicy
{
    internal static int StartStep(int steps, double control) =>
        (int)Math.Floor(steps * (1 - control));

    internal static bool IsQuantizedZeroPartial(int steps, double control) =>
        control < 1 && StartStep(steps, control) == 0;
}
