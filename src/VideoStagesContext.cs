using System.Runtime.CompilerServices;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;

namespace VideoStages;

internal static class VideoStagesContext
{
    private static readonly ConditionalWeakTable<WorkflowGenerator, VideoStagesSpec> Cache = new();

    public static VideoStagesSpec GetVideoStagesSpec(this WorkflowGenerator g) =>
        Cache.GetValue(g, key => new VideoStagesSpecParser(key).ParseConfig());

    public static bool TryGetVideoStagesSpec(this WorkflowGenerator g, out VideoStagesSpec spec) =>
        new VideoStagesSpecParser(g).TryParseConfig(out spec);

    public static List<ClipWithStages> GetClipsWithStages(this WorkflowGenerator g) =>
        new VideoStagesSpecParser(g).ParseClipsWithStages();

    public static List<StageSpec> GetActiveStages(this WorkflowGenerator g) =>
        new VideoStagesSpecParser(g).ParseStages();

    public static (int? Width, int? Height) GetRawJsonTopLevelDimensions(this WorkflowGenerator g) =>
        new VideoStagesSpecParser(g).GetRawJsonTopLevelDimensions();

    public static AudioFile GetUploadedAudioForClip(this WorkflowGenerator g, ClipSpec clip) =>
        new VideoStagesSpecParser(g).ParseUploadedAudioForClip(clip);

    public static int GetVideoStagesFps(this WorkflowGenerator g) =>
        g.GetVideoStagesSpec().FPS;
}
