using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;

namespace VideoStages;

internal sealed class ClipContext
{
    public ClipContext(JsonParser.ClipWithStages clip, WGNodeData sourceMedia, WGNodeData sourceVae)
    {
        Clip = clip;
        SourceMedia = sourceMedia;
        SourceVae = sourceVae;
    }

    public JsonParser.ClipWithStages Clip { get; }
    public WGNodeData SourceMedia { get; }
    public WGNodeData SourceVae { get; }
    public WanConditioningHandoff LastWanConditioningHandoff { get; set; }

    public bool IsFirstStage(JsonParser.StageSpec stage) =>
        Clip.Stages.Count > 0 && Clip.Stages[0].Id == stage.Id;
}

internal sealed record WanConditioningHandoff(
    int ClipId,
    JArray Positive,
    JArray Negative);
