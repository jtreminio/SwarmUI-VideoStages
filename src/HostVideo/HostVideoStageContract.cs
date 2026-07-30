using VideoStages.Planning;

namespace VideoStages.HostVideo;

/// <summary>
/// The proven common settings consumed by SwarmUI's stock video parameter seam. Architecture
/// payloads can carry additional facts, but WAN and generic-host stages both expose this subset.
/// </summary>
internal interface IHostVideoStageSettings
{
    string Model { get; }

    double Control { get; }

    int Steps { get; }

    double CfgScale { get; }

    string Sampler { get; }

    string Scheduler { get; }

    StageUpscalePlan Upscale { get; }
}

/// <summary>
/// Shared sampler-step arithmetic for stock-host decoded-video refinement.
/// </summary>
internal static class HostVideoStageSchedulePolicy
{
    internal static int StartStep(int steps, double control) =>
        (int)Math.Floor(steps * (1 - control));

    internal static bool IsQuantizedZeroPartial(int steps, double control) =>
        control < 1 && StartStep(steps, control) == 0;
}
