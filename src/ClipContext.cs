using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal sealed class ClipContext
{
    public ClipContext(ClipWithStages clip, WGNodeData sourceMedia, WGNodeData sourceVae)
    {
        Clip = clip;
        SourceMedia = sourceMedia;
        SourceVae = sourceVae;
    }

    public ClipWithStages Clip { get; }
    public WGNodeData SourceMedia { get; }
    public WGNodeData SourceVae { get; }
    public ConditioningHandoff LastConditioningHandoff { get; set; }
    public ClipAudioState AudioReuse { get; } = new();

    public bool IsFirstStage(StageSpec stage) =>
        Clip.Stages.Count > 0 && Clip.Stages[0].Id == stage.Id;
}

internal sealed record ConditioningHandoff(
    int ClipId,
    JArray Positive,
    JArray Negative);
