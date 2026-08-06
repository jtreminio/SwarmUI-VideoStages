namespace VideoStages.Planning;

internal static class StageStartStepPolicy
{
    internal static int StartStep(int steps, double control) =>
        (int)Math.Floor(steps * (1 - control));

    internal static bool PartialControlRoundsToZero(int steps, double control) =>
        control < 1 && StartStep(steps, control) == 0;
}
