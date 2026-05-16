using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal static class VideoStagesContext
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoStagesSpec> Cache = new();

    public static VideoStagesSpec GetVideoStagesSpec(this WorkflowGenerator g) =>
        Cache.GetValue(g, VideoStagesSpecParser.Parse);
}
